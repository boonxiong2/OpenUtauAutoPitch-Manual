using System;
using System.Collections.Generic;
using System.Linq;
using OpenUtau.Api;
using OpenUtau.Core.Ustx;
using OpenUtau.Core.Util;

namespace OpenUtau.Core {
    /// <summary>
    /// 让 CV（単独音）声库在一个音符块里塞多个音节。
    /// 歌词按空格或假名边界自动切分（"かきく" 和 "か き く" 等价），
    /// 每个音节各占一个 phoneme，依次平分音符时长；
    /// 单音节歌词时与 Default Phonemizer 行为一致。
    /// </summary>
    [Phonemizer("Japanese CV Multi-Syllable", "JA CV Multi", "far_control fork", language: "JA")]
    public class JapaneseCvMultiSyllablePhonemizer : Phonemizer {
        private USinger singer;

        public override void SetSinger(USinger singer) => this.singer = singer;

        public override Result Process(Note[] notes, Note? prev, Note? next, Note? prevNeighbour, Note? nextNeighbour, Note[] prevNeighbours) {
            var note = notes[0];
            var syllables = SplitLyrics.Split(note.lyric);
            if (syllables.Count == 0) {
                syllables.Add(note.lyric.Trim());
            }

            var attr0 = note.phonemeAttributes?.FirstOrDefault(attr => attr.index == 0) ?? default;
            string color = attr0.voiceColor ?? GetParentVoiceColor();
            int shift = attr0.toneShift ?? GetParentToneShift();
            int? alt = attr0.alternate ?? GetParentAlternate();

            int count = syllables.Count;
            var phonemes = new Phoneme[count];
            int lastPosition = -1;
            for (int i = 0; i < count; i++) {
                string alias = syllables[i];
                if (singer.TryGetMappedOto(alias + alt, note.tone + shift, color, out var oto)) {
                    alias = oto.Alias;
                }
                // position 是相对音符起点的 tick；音节平分时长，最后一个音节延伸到音符末尾。
                // 音符极短时四舍五入会撞到同一个 tick，这里强制递增，保证顺序严格上升。
                int position = (int)Math.Round((double)note.duration * i / count);
                if (position <= lastPosition) {
                    position = lastPosition + 1;
                }
                lastPosition = position;
                phonemes[i] = new Phoneme {
                    phoneme = alias,
                    position = position,
                };
            }
            return new Result {
                phonemes = phonemes,
            };
        }
    }
}
