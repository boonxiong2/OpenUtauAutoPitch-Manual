using System;
using System.Collections.Generic;
using System.Linq;
using OpenUtau.Core.Format;
using OpenUtau.Core.Ustx;

namespace OpenUtau.Core.Editing {
    /// <summary>
    /// 连接处气声：在相邻音符的衔接点（辅音入口）给 breathiness (BREC) 打一个小包，
    /// 而不是整句都加气 → 听起来像真人换气和咬字时漏的那点气息。
    /// BREC 由 Worldline 渲染器映射到 WORLD 的非周期性（ap），不需要额外采样。
    /// </summary>
    public class BreathAtJoins : BatchEdit {
        public virtual string Name => name;

        private string name;

        /// <summary>气包峰值（0~100，0 是干声，100 最气）。</summary>
        readonly int peak;
        /// <summary>气包半宽（毫秒）。</summary>
        readonly double widthMs;
        /// <summary>两音间距超过这个值（毫秒）就不算"连接"，不吸气。</summary>
        readonly double maxGapMs;

        public BreathAtJoins(int peak = 28, double widthMs = 50, double maxGapMs = 200) {
            name = "pianoroll.menu.notes.breathjoins";
            this.peak = Math.Clamp(peak, 0, 100);
            this.widthMs = widthMs;
            this.maxGapMs = maxGapMs;
        }

        public void Run(UProject project, UVoicePart part, List<UNote> selectedNotes, DocManager docManager) {
            var notes = (selectedNotes.Count > 0 ? selectedNotes : part.notes.ToList())
                .OrderBy(note => note.position)
                .ToList();
            if (notes.Count < 2) {
                return;
            }
            if (!project.expressions.ContainsKey(Format.Ustx.BREC)) {
                return;
            }
            int maxGap = MsToTick(project, maxGapMs);
            int width = Math.Max(UCurve.interval, MsToTick(project, widthMs));
            docManager.StartUndoGroup("command.batch.note", true);
            for (int i = 1; i < notes.Count; i++) {
                var note = notes[i];
                var prev = notes[i - 1];
                // 只在真正连上的地方吸气；休止符后面不吸。
                if (note.position - prev.End > maxGap) {
                    continue;
                }
                int onset = note.position;
                int x0 = Math.Max(0, onset - width);
                int x1 = onset + width;
                // 一个窄包：0 → peak → 0，只覆盖衔接点前后各 width。
                docManager.ExecuteCmd(new SetCurveCommand(project, part, Format.Ustx.BREC, x0, 0, x0, 0));
                docManager.ExecuteCmd(new SetCurveCommand(project, part, Format.Ustx.BREC, onset, peak, onset, peak));
                docManager.ExecuteCmd(new SetCurveCommand(project, part, Format.Ustx.BREC, x1, 0, x1, 0));
            }
            docManager.EndUndoGroup();
        }

        static int MsToTick(UProject project, double ms) {
            return (int)Math.Round((double)project.timeAxis.MsPosToTickPos(ms));
        }
    }
}
