using System;
using System.Collections.Generic;
using System.Linq;
using OpenUtau.Core.Format;
using OpenUtau.Core.Ustx;

namespace OpenUtau.Core.Editing {
    /// <summary>
    /// 连接处气声：按 SynthV 分离出来的真实气声分布给 BREC 打点。
    ///
    /// 实测（Signaling 干声 vs _Asp 轨，44.1k 单声道，按音符内位置分 10 段统计）：
    ///   音符开头 0-10%   气声 = 整体 1.52×
    ///   中段 20-50%      气声 = 整体 0.39×（最干）
    ///   音符末尾 70-80%  气声 = 整体 1.46×
    ///   末尾 90-100%     气声 = 整体 3.58×（最猛）
    /// 也就是说 SV 的气声是"每个音收尾时呼一口"，不是均匀铺。
    ///
    /// BREC 由 Worldline 渲染器映射到 WORLD 的 aperiodicity，不需要额外采样。
    /// </summary>
    public class BreathAtJoins : BatchEdit {
        public virtual string Name => name;

        private string name;

        /// <summary>音符开头气包峰值（0~100）。</summary>
        readonly int headPeak;
        /// <summary>音符末尾气包峰值（0~100）。实测末尾是开头的 2.3 倍。</summary>
        readonly int tailPeak;
        /// <summary>开头气包宽度（毫秒）。</summary>
        readonly double headMs;
        /// <summary>末尾气包宽度（毫秒）。</summary>
        readonly double tailMs;

        public BreathAtJoins(int headPeak = 22, int tailPeak = 48, double headMs = 45, double tailMs = 80) {
            name = "pianoroll.menu.notes.breathjoins";
            this.headPeak = Math.Clamp(headPeak, 0, 100);
            this.tailPeak = Math.Clamp(tailPeak, 0, 100);
            this.headMs = headMs;
            this.tailMs = tailMs;
        }

        public void Run(UProject project, UVoicePart part, List<UNote> selectedNotes, DocManager docManager) {
            var notes = (selectedNotes.Count > 0 ? selectedNotes : part.notes.ToList())
                .Where(note => !note.lyric.StartsWith("+"))
                .OrderBy(note => note.position)
                .ToList();
            if (notes.Count == 0) {
                return;
            }
            if (!project.expressions.ContainsKey(Format.Ustx.BREC)) {
                return;
            }
            int headTicks = Math.Max(UCurve.interval * 2, MsToTick(project, headMs));
            int tailTicks = Math.Max(UCurve.interval * 2, MsToTick(project, tailMs));
            docManager.StartUndoGroup("command.batch.note", true);
            foreach (var note in notes) {
                int durTicks = note.duration;
                // ---- 末尾气包：最响的位置在末尾 70% 处，收在音符结束点上
                int tailEnd = note.End;
                int tailStart = Math.Max(note.position, tailEnd - Math.Min(tailTicks, durTicks));
                if (tailEnd - tailStart >= UCurve.interval * 2) {
                    int peakAt = tailStart + (int)((tailEnd - tailStart) * 0.7);
                    docManager.ExecuteCmd(new SetCurveCommand(project, part, Format.Ustx.BREC, tailStart, 0, tailStart, 0));
                    docManager.ExecuteCmd(new SetCurveCommand(project, part, Format.Ustx.BREC, peakAt, tailPeak, peakAt, peakAt));
                    docManager.ExecuteCmd(new SetCurveCommand(project, part, Format.Ustx.BREC, tailEnd, 0, tailEnd, 0));
                }
                // ---- 开头气包：起音的辅音气，比尾巴轻一半
                int headStart = note.position;
                int headEnd = Math.Min(note.End, headStart + Math.Min(headTicks, durTicks));
                if (headEnd - headStart >= UCurve.interval * 2) {
                    docManager.ExecuteCmd(new SetCurveCommand(project, part, Format.Ustx.BREC, headStart, 0, headStart, 0));
                    docManager.ExecuteCmd(new SetCurveCommand(project, part, Format.Ustx.BREC, headStart + (headEnd - headStart) / 3, headPeak,
                        headStart + (headEnd - headStart) / 3, headStart + (headEnd - headStart) / 3));
                    docManager.ExecuteCmd(new SetCurveCommand(project, part, Format.Ustx.BREC, headEnd, 0, headEnd, 0));
                }
            }
            docManager.EndUndoGroup();
        }

        static int MsToTick(UProject project, double ms) {
            return (int)Math.Round((double)project.timeAxis.MsPosToTickPos(ms));
        }
    }
}
