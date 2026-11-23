using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenUtau.Core;
using OpenUtau.Core.Editing;
using OpenUtau.Core.Ustx;
using OpenUtau.Core.Util;
using Serilog;

namespace OpenUtau.Core.Editing {
    /// <summary>
    /// 自动音高（自动版）：歌词落地、音素化完成后，自动为该音符重新生成音高曲线。
    ///
    /// 与手动版的区别只在于"什么时候跑"：手动版由菜单点击触发，这里由歌词变化触发。
    /// 生成算法完全复用 <see cref="AutoPitch"/>，两版参数一致。
    ///
    /// 触发链：ChangeNoteLyricCommand →（等音素化）PhonemizedNotification → 防抖 → 生成。
    /// 之所以要等音素化：多音节音符（一个音符塞多个 CV）的音数取决于音素化结果，
    /// 在音素化之前生成会按错误的音节数算颤音。
    /// </summary>
    public sealed class AutoPitchAutoService : ICmdSubscriber {
        public static AutoPitchAutoService Inst { get; } = new();

        /// <summary>在批量写入曲线期间置位，避免自己产生的命令再次触发自己。</summary>
        internal static bool SuppressCallbacks;

        /// <summary>歌词改了但还没音素化时的兜底延迟；超过这个时间就先生成。</summary>
        const int LyricFallbackMs = 1200;
        /// <summary>音素化完成后再等一小会儿，让同批次的命令都落完。</summary>
        const int AfterPhonemizeMs = 80;

        readonly object scheduleLock = new();
        readonly Dictionary<UVoicePart, CancellationTokenSource> debounceTokens = new();
        readonly Dictionary<UVoicePart, HashSet<UNote>> pendingNotesByPart = new();
        readonly HashSet<UVoicePart> lyricPendingParts = new();

        public static bool IsEnabled => Preferences.Default.AutoPitchAuto;

        AutoPitchAutoService() { }

        public void Initialize() {
            DocManager.Inst.AddSubscriber(this);
        }

        public void OnNext(UCommand cmd, bool isUndo) {
            if (isUndo || SuppressCallbacks || !IsEnabled) {
                return;
            }
            if (cmd is ChangeNoteLyricCommand lyricCmd) {
                if (lyricCmd.Part == null || lyricCmd.Notes == null || lyricCmd.Notes.Length == 0) {
                    return;
                }
                lock (scheduleLock) {
                    lyricPendingParts.Add(lyricCmd.Part);
                    TrackAffectedNotes(lyricCmd.Part, lyricCmd.Notes);
                }
                SchedulePart(lyricCmd.Part, LyricFallbackMs);
                return;
            }
            if (cmd is PhonemizedNotification phonemized) {
                bool schedule;
                lock (scheduleLock) {
                    schedule = lyricPendingParts.Remove(phonemized.part);
                }
                if (schedule) {
                    SchedulePart(phonemized.part, AfterPhonemizeMs);
                }
            }
        }

        void TrackAffectedNotes(UVoicePart part, UNote[] notes) {
            if (notes == null || notes.Length == 0) {
                return;
            }
            if (!pendingNotesByPart.TryGetValue(part, out var set)) {
                set = new HashSet<UNote>();
                pendingNotesByPart[part] = set;
            }
            foreach (var note in notes) {
                if (note != null) {
                    set.Add(note);
                }
            }
        }

        void SchedulePart(UVoicePart part, int delayMs) {
            CancellationTokenSource cts;
            lock (scheduleLock) {
                if (debounceTokens.TryGetValue(part, out var existing)) {
                    existing.Cancel();
                    existing.Dispose();
                }
                cts = new CancellationTokenSource();
                debounceTokens[part] = cts;
            }
            var token = cts.Token;
            _ = Task.Run(async () => {
                try {
                    await Task.Delay(delayMs, token).ConfigureAwait(false);
                } catch (TaskCanceledException) {
                    return;
                }
                if (token.IsCancellationRequested) {
                    return;
                }
                lock (scheduleLock) {
                    if (debounceTokens.TryGetValue(part, out var current) && current == cts) {
                        debounceTokens.Remove(part);
                    }
                }
                RunForPart(part, token);
            });
        }

        void RunForPart(UVoicePart part, CancellationToken cancellationToken) {
            if (cancellationToken.IsCancellationRequested || !IsEnabled) {
                return;
            }
            var project = DocManager.Inst.Project;
            if (project == null || !project.parts.Contains(part)) {
                return;
            }
            List<UNote> affected;
            lock (scheduleLock) {
                if (!pendingNotesByPart.TryGetValue(part, out var set) || set.Count == 0) {
                    return;
                }
                affected = set.ToList();
                pendingNotesByPart.Remove(part);
            }
            // 只在当前 part 里的音符才处理（删掉的音符不能再写）
            affected = affected
                .Where(n => n != null && part.notes.Contains(n))
                .Distinct()
                .ToList();
            if (affected.Count == 0) {
                return;
            }
            try {
                SuppressCallbacks = true;
                var edit = new AutoPitch();
                edit.Run(project, part, affected, DocManager.Inst);
            } catch (Exception e) {
                Log.Warning(e, "Auto pitch (auto mode) failed.");
            } finally {
                SuppressCallbacks = false;
            }
        }
    }
}