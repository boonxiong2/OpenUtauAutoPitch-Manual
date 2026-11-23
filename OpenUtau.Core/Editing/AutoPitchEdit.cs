using System;
using System.Collections.Generic;
using System.Linq;
using OpenUtau.Core.Format;
using OpenUtau.Core.Ustx;
using OpenUtau.Core.Util;

namespace OpenUtau.Core.Editing {
    /// <summary>
    /// 自动音高（锚点版）。
    ///
    /// 为什么不用 pitd：OpenUtau 的界面按"每个音符自己的音高"为基准画 pitd，
    /// 音符一换基准，pitd 数值就整段跳（A 末尾 -500 → B 开头 +500），
    /// 画出来是 1000 音分的竖线，看着像掉坑。锚点（note.pitch）只在本音符内取值，
    /// 不会出现这种跨基准跳变。
    ///
    /// 参数由 .svp 实测反推（Singaling_SV，353 音符 / 8831 曲线点）：
    ///  · 起音 scoop：约七成音符不压（p50 ≈ 0），其余 −50~−210 音分，25~50ms 内爬上来
    ///  · 落音：基准 −42 音分，下个音更低时再深 0.25×音程（上限 −400）；
    ///         后接 >400ms 休止时至少 −130 音分。起点 60%、85% 到谷底，之后再回抬三成
    ///  · 慢漂移 8~12 音分 + 快漂移 4~7 音分（再大就听成跑调）
    ///  · 音准中心偏移 ±5 音分
    ///  · 毛刺噪声 ±6 音分（一阶自回归，惯性 0.7）
    ///  · 颤音 6.3Hz、深度 30~70 音分、前 15% 不颤
    ///  · 过渡五五分摊，可吃掉音符 65% 的长度（滑到中段才到位）
    ///  · 采样 12ms 一点（点太稀曲线会被连成直线，显得过分光滑）
    ///
    /// 单位提醒：PitchPoint.Y 是 0.1 半音（1.0 = 10 音分）。
    /// 这里所有幅度都先按"音分"算再 /10，早期版本直接把音分数字写进 Y，
    /// 结果起音和落音都只剩 1/10，听起来就是"一马平川"。
    /// </summary>
    public class AutoPitch : BatchEdit {
        public virtual string Name => name;

        private string name;

        readonly double strength;

        public AutoPitch(double strength = 1) {
            name = "pianoroll.menu.notes.autopitch";
            this.strength = Math.Max(0, strength);
        }

        public void Run(UProject project, UVoicePart part, List<UNote> selectedNotes, DocManager docManager) {
            var notes = (selectedNotes.Count > 0 ? selectedNotes : part.notes.ToList())
                .Where(note => !note.lyric.StartsWith("+"))
                .OrderBy(note => note.position)
                .ToList();
            if (notes.Count == 0) {
                return;
            }
            var shape = NotePresets.Default.DefaultPitchShape;
            var random = new Random(notes[0].position * 31 + notes.Count);
            docManager.StartUndoGroup("command.batch.note", true);
            // 之前跑过 pitd 版本的话必须清掉，否则和锚点叠加。
            // 注意不能用 PasteCurveCommand：它 Execute 里会取 xs[0]，空数组直接越界。
            if (project.expressions.ContainsKey(Format.Ustx.PITD)) {
                var existing = part.curves.FirstOrDefault(c => c.abbr == Format.Ustx.PITD);
                if (existing != null && existing.xs.Count > 0) {
                    docManager.ExecuteCmd(new OpenUtau.Core.MergedSetCurveCommand(
                        project, part, Format.Ustx.PITD,
                        existing.xs.ToArray(), existing.ys.ToArray(),
                        new int[0], new int[0]));
                }
            }
            // 气声点整段累积，循环结束后一次性写入 brec。
            // 不能每个音符各写一次：brec 是 part 级曲线，每次写都会覆盖整条，
            // 后一个音符会把前一个音符的气声抹掉。
            var brecXs = new List<int>();
            var brecYs = new List<int>();
            for (int i = 0; i < notes.Count; i++) {
                var note = notes[i];
                var prev = i > 0 ? notes[i - 1] : null;
                var next = i + 1 < notes.Count ? notes[i + 1] : null;
                double durMs = TickToMs(project, part, note.duration);
                if (durMs <= 5) {
                    continue;
                }
                bool legatoPrev = prev != null && Math.Abs(prev.End - note.position) <= 1;
                bool legatoNext = next != null && Math.Abs(next.position - note.End) <= 1;
                double gapAfter = next == null ? double.MaxValue : TickToMs(project, part, next.position - note.End);
                double gapBefore = prev == null ? double.MaxValue : TickToMs(project, part, note.position - prev.End);
                // 间隙 <=150ms 也当连着：两头分担落差，中间那段是缓斜线而不是垂直俯冲
                bool linkPrev = legatoPrev || (gapBefore >= 0 && gapBefore <= 150);
                bool linkNext = legatoNext || (gapAfter >= 0 && gapAfter <= 150);

                double deltaPrev = prev == null ? 0 : (prev.AdjustedTone - note.AdjustedTone) * 10;
                double deltaNext = next == null ? 0 : (next.AdjustedTone - note.AdjustedTone) * 10;
                double stepPrev = Math.Abs(deltaPrev) / 10;
                double stepNext = Math.Abs(deltaNext) / 10;
                // 滑音上限 1.5 半音太小：音程 7~11 半音时（deltaNext = -70 ~ -110），
                // 超出的部分全被挤到音符边界上，曲线在交界处一步跨完 —— 就是你看到的那条直线。
                // 现在放宽到 6 半音，并保证前后两段严格互补（-Δ/2 与 +Δ/2），中点绝对音高相等。
                const double slideCap = 60;

                // 锚点 Y 的单位是 0.1 半音（1.0 = 10 音分）。下面一律先算音分，最后 /10 换成 Y。
                // 起音（SV 实测）：约七成音符几乎不压（p50 = 0），其余压 −50 ~ −210 音分，25~50ms 内爬上来。
                double scoopCents = (random.NextDouble() < 0.7
                    ? 5 + random.NextDouble() * 25
                    : 50 + random.NextDouble() * 160) * strength;
                // 滑音必须两侧对称：只有两个音符都够长才滑，否则一边滑一边不滑，
                // 边界处就会留下一段纯垂直的落差。
                // 门槛从 120ms 降到 40ms：工程里有 27 个 63~94ms 的音符，
                // 旧门槛把它们排除在滑音之外，落差就只能全留在边界上（剩余 11 条小崖全是这种）。
                const double minSlideMs = 40;
                double durPrevMs = prev == null ? 0 : TickToMs(project, part, prev.duration);
                double durNextMs = next == null ? 0 : TickToMs(project, part, next.duration);
                bool slidePrev = linkPrev && durMs >= minSlideMs && durPrevMs >= minSlideMs;
                bool slideNext = linkNext && durMs >= minSlideMs && durNextMs >= minSlideMs;
                double startRel = slidePrev
                    ? Math.Clamp(deltaPrev * 0.5, -slideCap, slideCap)
                    : -scoopCents / 10.0;
                double endRel = slideNext
                    ? Math.Clamp(deltaNext * 0.5, -slideCap, slideCap)
                    : 0;
                // 滑音时长：必须给音符中段留出"稳定音高"。
                // 之前上限是音符的 85%，200~300ms 的音符从头滑到尾，中段一直不在基准音高上
                // （实测中段偏移 -97 音分，SV 只有 -6），听起来就是整句跑调。
                // 现在：滑入最多 45%、滑出最多 35%，同时各自不超过 150ms；两者相加不超过 70%。
                double maxIn = Math.Min(durMs * 0.45, 150);
                double maxOut = Math.Min(durMs * 0.35, 150);
                if (maxIn + maxOut > durMs * 0.70) {
                    double k = durMs * 0.70 / (maxIn + maxOut);
                    maxIn *= k;
                    maxOut *= k;
                }
                double glideIn = Math.Min(Math.Clamp(durMs * 0.30 + stepPrev * 10, 12, 150), maxIn);
                double glideOut = Math.Min(Math.Clamp(durMs * 0.25 + stepNext * 10, 12, 150), maxOut);
                if (slidePrev && stepPrev >= 2) {
                    glideIn = Math.Min(Math.Max(glideIn, 30 + stepPrev * 6), maxIn);
                }
                if (slideNext && stepNext >= 2) {
                    glideOut = Math.Min(Math.Max(glideOut, 30 + stepNext * 6), maxOut);
                }
                if (!slidePrev) {
                    // 断音的起音是"勾"不是"滑"：SV 实测 3~41ms（p50 21ms）就爬到位
                    glideIn = Math.Min(25 + random.NextDouble() * 25, durMs * 0.45);
                }

                // 落音（SV 实测，353 个音符）：基准 −42 音分；下个音更低时再深；
                // 后面接 >400ms 休止时至少 −130 音分（句末感）。
                // 两个关键修正：
                // 1) 连音时必须关掉落音。滑音（endRel）已经在把曲线往下一个音拉，
                //    再叠一层落音，两段独立下压就在边界处堆成落差——就是"大跳崖"。
                // 2) 深度要随时长缩放。旧版 125ms 的音符也压 200+ 音分，31ms 内掉完，
                //    斜率过陡，画出来就是一条竖直的崖。
                double fallCents = 42 + 2.5 * Math.Max(0, -deltaNext);
                if (gapAfter > 400) {
                    fallCents = Math.Max(fallCents, 130);
                }
                fallCents *= Math.Clamp(durMs / 300.0, 0.35, 1.2);
                fallCents = Math.Min(fallCents, 400) * (0.8 + random.NextDouble() * 0.7) * strength;
                // 落音只属于"句末"：只要后面还接着音（linkNext），末尾就不能往下掉。
                // 之前按能否滑音来决定是否落音，结果短连音不能滑、却仍保留落音，
                // 落差全部堆在边界上——就是那条竖直的大跳崖。
                if (linkNext) {
                    fallCents = 0;
                }
                double fallRel = -fallCents / 10.0;
                // 下降跨度：约 220 音分/100ms 的斜率上限，最多占音符一半。
                // 下限不能写死 60：音符短于 120ms 时 60 > durMs*0.5，Math.Clamp 会抛异常（'60' cannot be greater than 50）。
                // 改成上限的三分之一，保证 min <= max。
                double maxSpan = durMs * 0.5;
                double minSpan = Math.Min(60, maxSpan / 3);
                double fallSpan = fallCents <= 0 ? 0 : Math.Clamp(fallCents / 2.2, minSpan, maxSpan);
                double fallStart = fallCents <= 0 ? 1.0 : Math.Max(0.45, 1.0 - fallSpan / durMs);

                double driftA = (0.8 + random.NextDouble() * 0.4) * strength;
                double driftB = (0.4 + random.NextDouble() * 0.3) * strength;
                double driftTA = 420 + random.NextDouble() * 480;
                double driftTB = 130 + random.NextDouble() * 110;
                double driftPA = random.NextDouble() * Math.PI * 2;
                double driftPB = random.NextDouble() * Math.PI * 2;
                double centerOffset = (random.NextDouble() * 2 - 1) * 0.5 * strength;

                // 颤音（SV 实测）：长音符的振荡从音符很早处就有，短音符几乎没有；
                // 幅度随时长增大（250-400ms 中位 105~160 音分，200-250ms 中位 45 音分）。
                // 旧版固定从 15% 处起振、且 200ms 就开颤，短音上就是"莫名冒出一段颤音"。
                double longness = Math.Clamp((durMs - 250) / 550, 0, 1);
                double vibDepthCents = (20 + 60 * longness) * strength;
                double vibPeriodMs = 1000.0 / 6.3 + (random.NextDouble() * 16 - 8);
                // 起点：SV 的长音振荡最早从 10% 就开始，随音符变长略往后（18%~30%）
                double vibStart = durMs >= 400 ? 0.10 + random.NextDouble() * 0.08
                               : durMs >= 300 ? 0.14 + random.NextDouble() * 0.08
                               : 0.18 + random.NextDouble() * 0.10;
                double vibFade = Math.Min(durMs * 0.15, 60);   // 渐入时长，别让颤音硬顶上来
                bool hasVibrato = durMs >= 250;

                var syllables = SplitLyrics.Split(note.lyric);
                int sylCount = Math.Max(1, syllables.Count);
                bool multiSyllable = sylCount > 1;
                double sylMs = durMs / sylCount;

                double noise = 0;
                var points = new List<PitchPoint>();
                double step = multiSyllable ? 10 : 12;
                int steps = Math.Max(1, (int)Math.Floor(durMs / step));
                for (int si = 0; si <= steps; si++) {
                    double t = si == steps ? durMs : si * step;
                    double y = 0;
                    if (glideIn > 0 && t < glideIn) {
                        y += startRel * (1 - Smooth(t / glideIn));
                    }
                    if (endRel != 0 && t > durMs - glideOut) {
                        y += endRel * Smooth((t - (durMs - glideOut)) / glideOut);
                    }
                    if (fallRel != 0) {
                        double u = t / durMs;
                        if (u > fallStart) {
                            // 缓坡降下（S 形），不再模拟 SV 那种回抬——
                            // 回抬会在音符尾部翘起来，和下一个音的滑入撞在一起更像悬崖。
                            y += fallRel * Smooth((u - fallStart) / (1 - fallStart));
                        }
                    }
                    double driftWeight = Math.Min(1, t / Math.Max(durMs * 0.25, 1));
                    y += driftWeight * (driftA * Math.Sin(2 * Math.PI * t / driftTA + driftPA)
                                      + driftB * Math.Sin(2 * Math.PI * t / driftTB + driftPB));
                    y += centerOffset * Math.Min(1, t / 60);
                    if (hasVibrato) {
                        double vStart = durMs * vibStart;
                        if (t > vStart) {
                            double fade = Math.Min(1, (t - vStart) / Math.Max(vibFade, 1));
                            y += vibDepthCents / 10.0 * fade * Math.Sin(2 * Math.PI * (t - vStart) / vibPeriodMs);
                        }
                    }
                    noise = noise * 0.7 + (random.NextDouble() * 2 - 1) * 0.6 * strength;
                    y += noise;
                    points.Add(new PitchPoint((float)t, (float)y, shape));
                }
                // 关键：渲染时 note.vibrato 会独立叠加到锚点曲线上（ExpressionGraph 里 pitch = pitch_bend + vibrato）。
                // 原生颤音以「音符长度的 30%」为起点、满幅起振，如果不清掉，就会和下面写入的颤音叠成两层：
                // 长音符 70% 之后颤音突然变大、短音符半途冒出抖动，正是这个叠加造成的。
                // 本工程 333 个音符里 159 个 vibrato.length = 70（即从 30% 处开始颤），符合这个现象。
                if (note.vibrato.length > 0) {
                    docManager.ExecuteCmd(new VibratoLengthCommand(part, note, 0));
                }
                docManager.ExecuteCmd(new SetPitchPointsCommand(part, note, new UPitch {
                    data = points,
                    snapFirst = false,
                }));
                // 气声：从 .svp 导出的独立气声轨与干声轨实测得到。
                // 把两轨按 12ms 窗求 RMS，算「气声/干声」比值随音符进度的走向（63 个连音段、300+ 音符）：
                //   5%~63%   比值 0.95~1.01  → 平稳（气声恒定垫在底下，约 -18.7 dB）
                //   73%      1.05
                //   78%      1.20
                //   84%      1.84   ← 上冲
                //   89%      1.73
                // 所以形状是：全程铺一层薄气声，末尾 20% 抬到约 1.9 倍。
                // brec 是 part 级曲线、范围 -100~100、0 为默认。
                // 只给「后面真接了休止」的音符写：连音时下一音马上接上，加气声反而发糊。
                if (!linkNext && durMs >= 120) {
                    double peak = Math.Clamp(18 + durMs / 40.0, 18, 45) * strength;
                    double baseC = peak * 0.45;      // 中段铺底量
                    double msPerTick = TickToMs(project, part, note.duration) / Math.Max(note.duration, 1);
                    // 起点先压到 0，避免和上一个音符的曲线接成台阶
                    brecXs.Add(note.position);
                    brecYs.Add(0);
                    int startTick = note.position + (int)Math.Round(durMs * 0.10 / msPerTick);
                    brecXs.Add(startTick);
                    brecYs.Add((int)Math.Round(baseC));
                    // 末尾抬升：0.72~0.95 之间平滑爬到 peak
                    for (double ct = durMs * 0.72; ct <= durMs - 1; ct += 20) {
                        double p = ct / durMs;
                        double w = Smooth(Math.Clamp((p - 0.72) / 0.23, 0, 1));
                        brecXs.Add(note.position + (int)Math.Round(ct / msPerTick));
                        brecYs.Add((int)Math.Round(baseC + (peak - baseC) * w));
                    }
                    // 末尾收在峰值的七成，留一点回落，不要停在最高点
                    brecXs.Add(note.End);
                    brecYs.Add((int)Math.Round(baseC + (peak - baseC) * 0.75));
                }
            }
            // 气声统一写入：整条 brec 一次成型。
            // 先按 x 排序并去重，同位置只留最后一个（后写的音符优先）。
            if (brecXs.Count > 0) {
                var merged = brecXs
                    .Select((x, idx) => (x, y: brecYs[idx]))
                    .GroupBy(p => p.x)
                    .Select(g => g.Last())
                    .OrderBy(p => p.x)
                    .ToList();
                var oldCurve = part.curves.FirstOrDefault(c => c.abbr == Format.Ustx.BREC);
                docManager.ExecuteCmd(new OpenUtau.Core.MergedSetCurveCommand(
                    project, part, Format.Ustx.BREC,
                    oldCurve?.xs.ToArray() ?? new int[0],
                    oldCurve?.ys.ToArray() ?? new int[0],
                    merged.Select(p => p.x).ToArray(),
                    merged.Select(p => p.y).ToArray()));
            }
            docManager.EndUndoGroup();
        }

        static double Smooth(double x) {
            x = Math.Clamp(x, 0, 1);
            return x * x * (3 - 2 * x);
        }

        public double TickToMs(UProject project, UVoicePart part, int ticks) {
            return project.timeAxis.TickPosToMsPos(part.position + ticks) - project.timeAxis.TickPosToMsPos(part.position);
        }
    }
}
