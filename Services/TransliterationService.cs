using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Unidecode.NET;

namespace TaskbarMusic.Services
{
    /// <summary>
    /// Transliteration (romanization) service that converts non-Latin scripts
    /// to Latin characters. Detects language via Unicode ranges and applies
    /// the appropriate transliteration rules.
    ///
    /// Supported: Chinese (pinyin), Japanese (romaji), Korean (romanized),
    /// Hindi/Marathi/Nepali (Devanagari), Telugu, Tamil, Malayalam, Kannada.
    /// Fallback: Unidecode.NET for everything else.
    /// </summary>
    public static class TransliterationService
    {
        // ==================== LANGUAGE DETECTION ====================

        /// <summary>
        /// Detects the script/language of a text based on Unicode character ranges.
        /// </summary>
        public enum DetectedLang
        {
            Chinese, Japanese, Korean,
            Hindi, Marathi, Nepali,
            Telugu, Tamil, Malayalam, Kannada,
            Bengali, Gujarati, Gurmukhi, Odia, Sinhala,
            Thai, Lao, Tibetan, Myanmar, Khmer,
            Latin, Unknown
        }

        private static DetectedLang DetectLanguage(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return DetectedLang.Latin;

            int cjkCount = 0;
            int hiraganaCount = 0;
            int katakanaCount = 0;
            int hangulCount = 0;
            int devanagariCount = 0;
            int teluguCount = 0;
            int tamilCount = 0;
            int malayalamCount = 0;
            int kannadaCount = 0;
            int bengaliCount = 0;
            int gujaratiCount = 0;
            int gurmukhiCount = 0;
            int odiaCount = 0;
            int sinhalaCount = 0;
            int thaiCount = 0;
            int laoCount = 0;
            int tibetanCount = 0;
            int myanmarCount = 0;
            int khmerCount = 0;
            int totalNonAscii = 0;

            foreach (char c in text)
            {
                if (c <= 0x7F) continue;
                totalNonAscii++;

                if (c >= 0x4E00 && c <= 0x9FFF) cjkCount++;
                else if (c >= 0x3400 && c <= 0x4DBF) cjkCount++;
                else if (c >= 0xF900 && c <= 0xFAFF) cjkCount++;
                else if (c >= 0x3040 && c <= 0x309F) hiraganaCount++;
                else if (c >= 0x30A0 && c <= 0x30FF) katakanaCount++;
                else if (c >= 0xAC00 && c <= 0xD7AF) hangulCount++;
                else if (c >= 0x1100 && c <= 0x11FF) hangulCount++;
                else if (c >= 0x0900 && c <= 0x097F) devanagariCount++;
                else if (c >= 0x0C00 && c <= 0x0C7F) teluguCount++;
                else if (c >= 0x0B80 && c <= 0x0BFF) tamilCount++;
                else if (c >= 0x0D00 && c <= 0x0D7F) malayalamCount++;
                else if (c >= 0x0C80 && c <= 0x0CFF) kannadaCount++;
                else if (c >= 0x0980 && c <= 0x09FF) bengaliCount++;
                else if (c >= 0x0A80 && c <= 0x0AFF) gujaratiCount++;
                else if (c >= 0x0A00 && c <= 0x0A7F) gurmukhiCount++;
                else if (c >= 0x0B00 && c <= 0x0B7F) odiaCount++;
                else if (c >= 0x0D80 && c <= 0x0DFF) sinhalaCount++;
                else if (c >= 0x0E00 && c <= 0x0E7F) thaiCount++;
                else if (c >= 0x0E80 && c <= 0x0EFF) laoCount++;
                else if (c >= 0x0F00 && c <= 0x0FFF) tibetanCount++;
                else if (c >= 0x1000 && c <= 0x109F) myanmarCount++;
                else if (c >= 0x1780 && c <= 0x17FF) khmerCount++;
            }

            if (totalNonAscii == 0) return DetectedLang.Latin;

            if (hiraganaCount > 0 || katakanaCount > 0) return DetectedLang.Japanese;
            if (hangulCount > 0) return DetectedLang.Korean;
            if (cjkCount > 0) return DetectedLang.Chinese;

            LogMsg($"DetectLanguage counters: deva={devanagariCount} tel={teluguCount} tam={tamilCount} mal={malayalamCount} kan={kannadaCount} ben={bengaliCount} guj={gujaratiCount} gur={gurmukhiCount} odia={odiaCount} sinh={sinhalaCount} thai={thaiCount} lao={laoCount} tib={tibetanCount} mya={myanmarCount} khm={khmerCount} totalNonAscii={totalNonAscii} textStart='{TruncateLog(text, 60)}'");

            // For Indic and SE Asian scripts (non-overlapping), use DOMINANT script (max count)
            int maxIndic = new[] {
                devanagariCount, teluguCount, tamilCount,
                malayalamCount, kannadaCount,
                bengaliCount, gujaratiCount, gurmukhiCount,
                odiaCount, sinhalaCount,
                thaiCount, laoCount, tibetanCount,
                myanmarCount, khmerCount
            }.Max();

            if (maxIndic > 0)
            {
                if (devanagariCount == maxIndic)
                {
                    var lower = text.ToLowerInvariant();
                    if (lower.Contains("ने") || lower.Contains("को") || lower.Contains("मा"))
                        return DetectedLang.Nepali;
                    if (lower.Contains("चा") || lower.Contains("ची") || lower.Contains("तो") || lower.Contains("ही"))
                        return DetectedLang.Marathi;
                    return DetectedLang.Hindi;
                }
                if (teluguCount == maxIndic) return DetectedLang.Telugu;
                if (tamilCount == maxIndic) return DetectedLang.Tamil;
                if (malayalamCount == maxIndic) return DetectedLang.Malayalam;
                if (kannadaCount == maxIndic) return DetectedLang.Kannada;
                if (bengaliCount == maxIndic) return DetectedLang.Bengali;
                if (gujaratiCount == maxIndic) return DetectedLang.Gujarati;
                if (gurmukhiCount == maxIndic) return DetectedLang.Gurmukhi;
                if (odiaCount == maxIndic) return DetectedLang.Odia;
                if (sinhalaCount == maxIndic) return DetectedLang.Sinhala;
                if (thaiCount == maxIndic) return DetectedLang.Thai;
                if (laoCount == maxIndic) return DetectedLang.Lao;
                if (tibetanCount == maxIndic) return DetectedLang.Tibetan;
                if (myanmarCount == maxIndic) return DetectedLang.Myanmar;
                if (khmerCount == maxIndic) return DetectedLang.Khmer;
            }

            return DetectedLang.Unknown;
        }

        // ==================== JAPANESE OVERRIDES ====================

        private static readonly Dictionary<string, string> JapaneseOverrides = new()
        {
            ["愛してる"] = "aishiteru",
            ["愛している"] = "aishiteiru",
            ["大好き"] = "daisuki",
            ["ありがとう"] = "arigatou",
            ["こんにちは"] = "konnichiwa",
            ["さようなら"] = "sayounara",
            ["こんばんは"] = "konbanwa",
            ["おはよう"] = "ohayou",
            ["おやすみ"] = "oyasumi",
            ["いただきます"] = "itadakimasu",
            ["ごちそうさま"] = "gochisousama",
            ["すみません"] = "sumimasen",
            ["お願い"] = "onegai",
            ["ください"] = "kudasai",
            ["大丈夫"] = "daijoubu",
            ["わかりました"] = "wakarimashita",
            ["すごい"] = "sugoi",
            ["かわいい"] = "kawaii",
            ["かっこいい"] = "kakkoii",
            ["やばい"] = "yabai",
        };

        // ==================== JAPANESE KANA → ROMAJI ====================

        private static readonly Dictionary<string, string> HiraganaMap = new()
        {
            ["あ"] = "a", ["い"] = "i", ["う"] = "u", ["え"] = "e", ["お"] = "o",
            ["か"] = "ka", ["き"] = "ki", ["く"] = "ku", ["け"] = "ke", ["こ"] = "ko",
            ["さ"] = "sa", ["し"] = "shi", ["す"] = "su", ["せ"] = "se", ["そ"] = "so",
            ["た"] = "ta", ["ち"] = "chi", ["つ"] = "tsu", ["て"] = "te", ["と"] = "to",
            ["な"] = "na", ["に"] = "ni", ["ぬ"] = "nu", ["ね"] = "ne", ["の"] = "no",
            ["は"] = "ha", ["ひ"] = "hi", ["ふ"] = "fu", ["へ"] = "he", ["ほ"] = "ho",
            ["ま"] = "ma", ["み"] = "mi", ["む"] = "mu", ["め"] = "me", ["も"] = "mo",
            ["や"] = "ya", ["ゆ"] = "yu", ["よ"] = "yo",
            ["ら"] = "ra", ["り"] = "ri", ["る"] = "ru", ["れ"] = "re", ["ろ"] = "ro",
            ["わ"] = "wa", ["を"] = "wo", ["ん"] = "n",
            ["が"] = "ga", ["ぎ"] = "gi", ["ぐ"] = "gu", ["げ"] = "ge", ["ご"] = "go",
            ["ざ"] = "za", ["じ"] = "ji", ["ず"] = "zu", ["ぜ"] = "ze", ["ぞ"] = "zo",
            ["だ"] = "da", ["ぢ"] = "ji", ["づ"] = "zu", ["で"] = "de", ["ど"] = "do",
            ["ば"] = "ba", ["び"] = "bi", ["ぶ"] = "bu", ["べ"] = "be", ["ぼ"] = "bo",
            ["ぱ"] = "pa", ["ぴ"] = "pi", ["ぷ"] = "pu", ["ぺ"] = "pe", ["ぽ"] = "po",
            ["ゃ"] = "ya", ["ゅ"] = "yu", ["ょ"] = "yo",
            ["ゐ"] = "i", ["ゑ"] = "we",
        };

        private static readonly Dictionary<string, string> KatakanaMap = new()
        {
            ["ア"] = "a", ["イ"] = "i", ["ウ"] = "u", ["エ"] = "e", ["オ"] = "o",
            ["カ"] = "ka", ["キ"] = "ki", ["ク"] = "ku", ["ケ"] = "ke", ["コ"] = "ko",
            ["サ"] = "sa", ["シ"] = "shi", ["ス"] = "su", ["セ"] = "se", ["ソ"] = "so",
            ["タ"] = "ta", ["チ"] = "chi", ["ツ"] = "tsu", ["テ"] = "te", ["ト"] = "to",
            ["ナ"] = "na", ["ニ"] = "ni", ["ヌ"] = "nu", ["ネ"] = "ne", ["ノ"] = "no",
            ["ハ"] = "ha", ["ヒ"] = "hi", ["フ"] = "fu", ["ヘ"] = "he", ["ホ"] = "ho",
            ["マ"] = "ma", ["ミ"] = "mi", ["ム"] = "mu", ["メ"] = "me", ["モ"] = "mo",
            ["ヤ"] = "ya", ["ユ"] = "yu", ["ヨ"] = "yo",
            ["ラ"] = "ra", ["リ"] = "ri", ["ル"] = "ru", ["レ"] = "re", ["ロ"] = "ro",
            ["ワ"] = "wa", ["ヲ"] = "wo", ["ン"] = "n",
            ["ガ"] = "ga", ["ギ"] = "gi", ["グ"] = "gu", ["ゲ"] = "ge", ["ゴ"] = "go",
            ["ザ"] = "za", ["ジ"] = "ji", ["ズ"] = "zu", ["ゼ"] = "ze", ["ゾ"] = "zo",
            ["ダ"] = "da", ["ヂ"] = "ji", ["ヅ"] = "zu", ["デ"] = "de", ["ド"] = "do",
            ["バ"] = "ba", ["ビ"] = "bi", ["ブ"] = "bu", ["ベ"] = "be", ["ボ"] = "bo",
            ["パ"] = "pa", ["ピ"] = "pi", ["プ"] = "pu", ["ペ"] = "pe", ["ポ"] = "po",
            ["ャ"] = "ya", ["ュ"] = "yu", ["ョ"] = "yo",
            ["ヴ"] = "vu", ["ァ"] = "a", ["ィ"] = "i", ["ゥ"] = "u", ["ェ"] = "e", ["ォ"] = "o",
            ["ヮ"] = "wa",
            ["ヰ"] = "i", ["ヱ"] = "we",
        };

        private static readonly Dictionary<string, string> YouonMap = new()
        {
            ["きゃ"] = "kya", ["きゅ"] = "kyu", ["きょ"] = "kyo",
            ["しゃ"] = "sha", ["しゅ"] = "shu", ["しょ"] = "sho",
            ["ちゃ"] = "cha", ["ちゅ"] = "chu", ["ちょ"] = "cho",
            ["にゃ"] = "nya", ["にゅ"] = "nyu", ["にょ"] = "nyo",
            ["ひゃ"] = "hya", ["ひゅ"] = "hyu", ["ひょ"] = "hyo",
            ["みゃ"] = "mya", ["みゅ"] = "myu", ["みょ"] = "myo",
            ["りゃ"] = "rya", ["りゅ"] = "ryu", ["りょ"] = "ryo",
            ["ぎゃ"] = "gya", ["ぎゅ"] = "gyu", ["ぎょ"] = "gyo",
            ["じゃ"] = "ja", ["じゅ"] = "ju", ["じょ"] = "jo",
            ["びゃ"] = "bya", ["びゅ"] = "byu", ["びょ"] = "byo",
            ["ぴゃ"] = "pya", ["ぴゅ"] = "pyu", ["ぴょ"] = "pyo",
            ["キャ"] = "kya", ["キュ"] = "kyu", ["キョ"] = "kyo",
            ["シャ"] = "sha", ["シュ"] = "shu", ["ショ"] = "sho",
            ["チャ"] = "cha", ["チュ"] = "chu", ["チョ"] = "cho",
            ["ニャ"] = "nya", ["ニュ"] = "nyu", ["ニョ"] = "nyo",
            ["ヒャ"] = "hya", ["ヒュ"] = "hyu", ["ヒョ"] = "hyo",
            ["ミャ"] = "mya", ["ミュ"] = "myu", ["ミョ"] = "myo",
            ["リャ"] = "rya", ["リュ"] = "ryu", ["リョ"] = "ryo",
            ["ギャ"] = "gya", ["ギュ"] = "gyu", ["ギョ"] = "gyo",
            ["ジャ"] = "ja", ["ジュ"] = "ju", ["ジョ"] = "jo",
            ["ビャ"] = "bya", ["ビュ"] = "byu", ["ビョ"] = "byo",
            ["ピャ"] = "pya", ["ピュ"] = "pyu", ["ピョ"] = "pyo",
        };

        private static string JapaneseToRomaji(string text)
        {
            if (JapaneseOverrides.TryGetValue(text, out var overrideResult))
                return overrideResult;

            var sb = new StringBuilder();
            int i = 0;

            while (i < text.Length)
            {
                string ch = text[i].ToString();

                // Check 3-char youon
                if (i + 2 < text.Length)
                {
                    var tri = text.Substring(i, 3);
                    if (YouonMap.TryGetValue(tri, out var youon))
                    {
                        sb.Append(youon);
                        i += 3;
                        continue;
                    }
                }

                // Check 2-char youon
                if (i + 1 < text.Length)
                {
                    var pair = text.Substring(i, 2);
                    if (YouonMap.TryGetValue(pair, out var youon2))
                    {
                        sb.Append(youon2);
                        i += 2;
                        continue;
                    }

                    // Handle geminate (small つ/ッ)
                    if ((ch == "っ" || ch == "ッ") && i + 1 < text.Length)
                    {
                        var nextCh = text[i + 1].ToString();
                        if (HiraganaMap.TryGetValue(nextCh, out var nextRomaji) && nextRomaji.Length > 0)
                        {
                            sb.Append(nextRomaji[0]).Append(nextRomaji);
                            i += 2;
                            continue;
                        }
                        if (KatakanaMap.TryGetValue(nextCh, out var nextRomajiK) && nextRomajiK.Length > 0)
                        {
                            sb.Append(nextRomajiK[0]).Append(nextRomajiK);
                            i += 2;
                            continue;
                        }
                    }
                }

                if (HiraganaMap.TryGetValue(ch, out var romaji))
                {
                    sb.Append(romaji);
                }
                else if (KatakanaMap.TryGetValue(ch, out var romajiK))
                {
                    sb.Append(romajiK);
                }
                else
                {
                    sb.Append(ch.Unidecode());
                }

                i++;
            }

            return sb.ToString();
        }

        // ==================== KOREAN HANGUL → ROMANIZATION ====================

        private const int SBase = 0xAC00;
        private const int LCount = 19;
        private const int VCount = 21;
        private const int TCount = 28;
        private const int NCount = VCount * TCount;
        private const int SCount = LCount * NCount;

        private static readonly string[] Choseong =
        {
            "g", "kk", "n", "d", "tt", "r", "m", "b", "pp",
            "s", "ss", "", "j", "jj", "ch", "k", "t", "p", "h"
        };

        private static readonly string[] Jungseong =
        {
            "a", "ae", "ya", "yae", "eo", "e", "yeo", "ye", "o", "wa", "wae",
            "oe", "yo", "u", "wo", "we", "wi", "yu", "eu", "ui", "i"
        };

        private static readonly string[] Jongseong =
        {
            "", "g", "kk", "gs", "n", "nj", "nh", "d", "l", "lg", "lm",
            "lb", "ls", "lt", "lp", "lh", "m", "b", "bs", "s", "ss", "ng",
            "j", "ch", "k", "t", "p", "h"
        };

        private static string KoreanToRomanized(string text)
        {
            var sb = new StringBuilder();

            foreach (char c in text)
            {
                int code = c;

                if (code >= SBase && code < SBase + SCount)
                {
                    int sIndex = code - SBase;
                    int lIndex = sIndex / NCount;
                    int vIndex = (sIndex % NCount) / TCount;
                    int tIndex = sIndex % TCount;

                    sb.Append(Choseong[lIndex]);
                    sb.Append(Jungseong[vIndex]);
                    sb.Append(Jongseong[tIndex]);
                }
                else
                {
                    sb.Append(c.Unidecode());
                }
            }

            return sb.ToString();
        }

        // ==================== CHINESE → PINYIN ====================

        private static readonly Dictionary<string, string> ChinesePinyinMap = new()
        {
            ["的"] = "de", ["一"] = "yi", ["是"] = "shi", ["在"] = "zai", ["不"] = "bu",
            ["了"] = "le", ["有"] = "you", ["和"] = "he", ["人"] = "ren", ["这"] = "zhe",
            ["中"] = "zhong", ["大"] = "da", ["为"] = "wei", ["上"] = "shang", ["个"] = "ge",
            ["国"] = "guo", ["我"] = "wo", ["以"] = "yi", ["要"] = "yao", ["他"] = "ta",
            ["时"] = "shi", ["来"] = "lai", ["用"] = "yong", ["们"] = "men", ["生"] = "sheng",
            ["到"] = "dao", ["作"] = "zuo", ["地"] = "de", ["于"] = "yu", ["出"] = "chu",
            ["就"] = "jiu", ["分"] = "fen", ["对"] = "dui", ["成"] = "cheng", ["会"] = "hui",
            ["可"] = "ke", ["主"] = "zhu", ["发"] = "fa", ["年"] = "nian", ["动"] = "dong",
            ["同"] = "tong", ["工"] = "gong", ["也"] = "ye", ["能"] = "neng", ["下"] = "xia",
            ["过"] = "guo", ["子"] = "zi", ["说"] = "shuo", ["产"] = "chan", ["种"] = "zhong",
            ["面"] = "mian", ["而"] = "er", ["方"] = "fang", ["后"] = "hou", ["多"] = "duo",
            ["定"] = "ding", ["行"] = "xing", ["学"] = "xue", ["法"] = "fa", ["所"] = "suo",
            ["民"] = "min", ["得"] = "de", ["经"] = "jing", ["十"] = "shi", ["三"] = "san",
            ["之"] = "zhi", ["进"] = "jin", ["着"] = "zhe", ["等"] = "deng", ["部"] = "bu",
            ["度"] = "du", ["家"] = "jia", ["电"] = "dian", ["力"] = "li", ["里"] = "li",
            ["如"] = "ru", ["水"] = "shui", ["化"] = "hua", ["高"] = "gao", ["自"] = "zi",
            ["二"] = "er", ["理"] = "li", ["起"] = "qi", ["小"] = "xiao", ["物"] = "wu",
            ["现"] = "xian", ["实"] = "shi", ["加"] = "jia", ["量"] = "liang", ["都"] = "du",
            ["两"] = "liang", ["体"] = "ti", ["制"] = "zhi", ["机"] = "ji", ["当"] = "dang",
            ["使"] = "shi", ["点"] = "dian", ["从"] = "cong", ["业"] = "ye", ["本"] = "ben",
            ["去"] = "qu", ["把"] = "ba", ["性"] = "xing", ["好"] = "hao", ["应"] = "ying",
            ["开"] = "kai", ["它"] = "ta", ["合"] = "he", ["还"] = "hai", ["因"] = "yin",
            ["由"] = "you", ["其"] = "qi", ["些"] = "xie", ["然"] = "ran", ["前"] = "qian",
            ["外"] = "wai", ["天"] = "tian", ["政"] = "zheng", ["四"] = "si", ["日"] = "ri",
            ["那"] = "na", ["社"] = "she", ["义"] = "yi", ["事"] = "shi", ["平"] = "ping",
            ["形"] = "xing", ["相"] = "xiang", ["全"] = "quan", ["表"] = "biao", ["间"] = "jian",
            ["样"] = "yang", ["与"] = "yu", ["关"] = "guan", ["各"] = "ge", ["重"] = "zhong",
            ["新"] = "xin", ["线"] = "xian", ["内"] = "nei", ["数"] = "shu", ["正"] = "zheng",
            ["什"] = "shen", ["话"] = "hua", ["反"] = "fan", ["你"] = "ni", ["明"] = "ming",
            ["看"] = "kan", ["原"] = "yuan", ["又"] = "you", ["么"] = "me", ["比"] = "bi",
            ["战"] = "zhan", ["但"] = "dan", ["任"] = "ren", ["今"] = "jin", ["保"] = "bao",
            ["元"] = "yuan", ["更"] = "geng", ["她"] = "ta", ["处"] = "chu", ["将"] = "jiang",
            ["第"] = "di", ["做"] = "zuo", ["无"] = "wu", ["被"] = "bei",
            ["老"] = "lao", ["师"] = "shi", ["文"] = "wen", ["字"] = "zi", ["道"] = "dao",
            ["爱"] = "ai", ["想"] = "xiang", ["该"] = "gai", ["手"] = "shou",
            ["太"] = "tai", ["心"] = "xin", ["只"] = "zhi", ["像"] = "xiang", ["常"] = "chang",
            ["告"] = "gao", ["路"] = "lu", ["安"] = "an", ["许"] = "xu", ["条"] = "tiao",
            ["别"] = "bie", ["风"] = "feng", ["至"] = "zhi", ["每"] = "mei", ["给"] = "gei",
            ["火"] = "huo", ["长"] = "chang", ["让"] = "rang", ["位"] = "wei", ["士"] = "shi",
            ["少"] = "shao", ["知"] = "zhi", ["己"] = "ji", ["月"] = "yue",
            ["入"] = "ru", ["山"] = "shan", ["活"] = "huo", ["头"] = "tou",
            ["市"] = "shi", ["白"] = "bai", ["世"] = "shi", ["万"] = "wan", ["即"] = "ji",
            ["目"] = "mu", ["边"] = "bian", ["问"] = "wen", ["军"] = "jun", ["最"] = "zui",
            ["立"] = "li", ["南"] = "nan", ["名"] = "ming", ["总"] = "zong", ["连"] = "lian",
            ["教"] = "jiao", ["意"] = "yi", ["北"] = "bei", ["东"] = "dong",
            ["打"] = "da", ["报"] = "bao", ["光"] = "guang", ["公"] = "gong", ["运"] = "yun",
            ["通"] = "tong", ["向"] = "xiang", ["西"] = "xi", ["具"] = "ju",
            ["车"] = "che", ["特"] = "te", ["难"] = "nan", ["金"] = "jin", ["门"] = "men",
            ["声"] = "sheng", ["海"] = "hai", ["男"] = "nan", ["女"] = "nv",
            ["笑"] = "xiao", ["哭"] = "ku", ["走"] = "zou", ["跑"] = "pao",
            ["读"] = "du", ["写"] = "xie", ["唱"] = "chang", ["听"] = "ting", ["见"] = "jian",
            ["食"] = "shi", ["喝"] = "he", ["吃"] = "chi", ["完"] = "wan", ["住"] = "zhu",
            ["买"] = "mai", ["卖"] = "mai", ["花"] = "hua", ["钱"] = "qian", ["红"] = "hong",
            ["绿"] = "lv", ["蓝"] = "lan", ["黄"] = "huang", ["黑"] = "hei",
            ["星"] = "xing", ["空"] = "kong", ["云"] = "yun", ["雨"] = "yu", ["雪"] = "xue",
            ["草"] = "cao", ["树"] = "shu", ["叶"] = "ye", ["果"] = "guo",
            ["快"] = "kuai", ["慢"] = "man", ["热"] = "re", ["冷"] = "leng", ["暖"] = "nuan",
            ["真"] = "zhen", ["假"] = "jia", ["坏"] = "huai", ["美"] = "mei",
            ["丽"] = "li", ["漂"] = "piao", ["亮"] = "liang", ["暗"] = "an", ["深"] = "shen",
            ["浅"] = "qian", ["高"] = "gao", ["低"] = "di", ["短"] = "duan",
            ["远"] = "yuan", ["近"] = "jin", ["前"] = "qian", ["后"] = "hou", ["左"] = "zuo",
            ["右"] = "you", ["上"] = "shang", ["下"] = "xia", ["东"] = "dong", ["西"] = "xi",
            ["亲"] = "qin", ["朋"] = "peng", ["友"] = "you", ["家"] = "jia",
            ["世"] = "shi", ["界"] = "jie", ["球"] = "qiu", ["阳"] = "yang",
            ["晚"] = "wan", ["早"] = "zao", ["春"] = "chun", ["夏"] = "xia", ["秋"] = "qiu",
            ["冬"] = "dong", ["年"] = "nian", ["月"] = "yue", ["日"] = "ri",
            ["分"] = "fen", ["秒"] = "miao", ["天"] = "tian", ["地"] = "di",
            ["大"] = "da", ["小"] = "xiao", ["多"] = "duo", ["少"] = "shao",
            ["二"] = "er", ["三"] = "san", ["四"] = "si", ["五"] = "wu", ["六"] = "liu",
            ["七"] = "qi", ["八"] = "ba", ["九"] = "jiu", ["十"] = "shi", ["百"] = "bai",
            ["千"] = "qian", ["万"] = "wan", ["亿"] = "yi", ["半"] = "ban", ["零"] = "ling",
            ["点"] = "dian", ["次"] = "ci", ["回"] = "hui", ["遍"] = "bian",
            ["条"] = "tiao", ["件"] = "jian", ["张"] = "zhang",
            ["双"] = "shuang", ["副"] = "fu",
            ["片"] = "pian", ["块"] = "kuai", ["首"] = "shou", ["支"] = "zhi",
            ["篇"] = "pian", ["页"] = "ye", ["句"] = "ju", ["段"] = "duan",
            ["曲"] = "qu", ["歌"] = "ge", ["词"] = "ci", ["乐"] = "le", ["音"] = "yin",
            ["舞"] = "wu", ["画"] = "hua", ["诗"] = "shi", ["梦"] = "meng", ["心"] = "xin",
            ["情"] = "qing", ["爱"] = "ai", ["恨"] = "hen", ["喜"] = "xi", ["怒"] = "nu",
            ["哀"] = "ai", ["思"] = "si", ["念"] = "nian",
            ["记"] = "ji", ["忘"] = "wang", ["忆"] = "yi", ["懂"] = "dong", ["感"] = "gan",
            ["觉"] = "jue", ["知"] = "zhi", ["道"] = "dao", ["愿"] = "yuan", ["希"] = "xi",
            ["望"] = "wang", ["切"] = "qie",
            ["没"] = "mei",
            ["来"] = "lai", ["去"] = "qu",
            ["能"] = "neng", ["会"] = "hui", ["可"] = "ke", ["以"] = "yi", ["应"] = "ying",
            ["得"] = "de", ["着"] = "zhao", ["过"] = "guo",
            ["被"] = "bei", ["让"] = "rang",
            ["从"] = "cong",
            ["和"] = "he", ["跟"] = "gen",
            ["与"] = "yu", ["同"] = "tong", ["比"] = "bi", ["但"] = "dan", ["而"] = "er",
            ["或"] = "huo", ["如"] = "ru", ["果"] = "guo", ["虽"] = "sui",
            ["因"] = "yin", ["为"] = "wei",
            ["之"] = "zhi",
            ["吧"] = "ba", ["吗"] = "ma", ["呢"] = "ne",
            ["啊"] = "a", ["哦"] = "o", ["嗯"] = "en", ["哈"] = "ha",
        };

        private static string ChineseToPinyin(string text)
        {
            var sb = new StringBuilder();
            bool lastWasWord = false;

            foreach (char c in text)
            {
                var key = c.ToString();

                if (ChinesePinyinMap.TryGetValue(key, out var pinyin))
                {
                    if (lastWasWord) sb.Append(' ');
                    sb.Append(pinyin);
                    lastWasWord = true;
                }
                else if (c >= 0x4E00 && c <= 0x9FFF)
                {
                    var fallback = c.Unidecode().Trim();
                    if (!string.IsNullOrEmpty(fallback))
                    {
                        if (lastWasWord) sb.Append(' ');
                        sb.Append(fallback);
                        lastWasWord = true;
                    }
                }
                else
                {
                    sb.Append(c);
                    lastWasWord = false;
                }
            }

            return sb.ToString();
        }

        // ==================== INDIC SCRIPTS → HARVARD-KYOTO ====================

        // All Indic maps use string keys to handle multi-codepoint characters safely.
        // Harvard-Kyoto (HK) scheme: uppercase = long vowel, ~N = velar/ palatal nasal, etc.

        private static readonly Dictionary<string, string> DevanagariHK = new()
        {
            ["अ"] = "a", ["आ"] = "A", ["इ"] = "i", ["ई"] = "I", ["उ"] = "u", ["ऊ"] = "U",
            ["ऋ"] = "RRi", ["ॠ"] = "RRI", ["ऌ"] = "LLi", ["ॡ"] = "LLI",
            ["ए"] = "e", ["ऐ"] = "ai", ["ओ"] = "o", ["औ"] = "au",
            ["क"] = "ka", ["ख"] = "kha", ["ग"] = "ga", ["घ"] = "gha", ["ङ"] = "~Na",
            ["च"] = "ca", ["छ"] = "cha", ["ज"] = "ja", ["झ"] = "jha", ["ञ"] = "~na",
            ["ट"] = "Ta", ["ठ"] = "Tha", ["ड"] = "Da", ["ढ"] = "Dha", ["ण"] = "Na",
            ["त"] = "ta", ["थ"] = "tha", ["द"] = "da", ["ध"] = "dha", ["न"] = "na",
            ["प"] = "pa", ["फ"] = "pha", ["ब"] = "ba", ["भ"] = "bha", ["म"] = "ma",
            ["य"] = "ya", ["र"] = "ra", ["ल"] = "la", ["व"] = "va",
            ["श"] = "za", ["ष"] = "Sa", ["स"] = "sa", ["ह"] = "ha",
            ["ळ"] = "La", ["क्ष"] = "kSa", ["ज्ञ"] = "j~na",
            ["ा"] = "A", ["ि"] = "i", ["ी"] = "I", ["ु"] = "u", ["ू"] = "U",
            ["ृ"] = "RRi", ["ॄ"] = "RRI", ["े"] = "e", ["ै"] = "ai", ["ो"] = "o", ["ौ"] = "au",
            ["ं"] = "M", ["ः"] = "H", ["ँ"] = "M",
            ["्"] = "",  // virama/halant — skip silently
            ["ॐ"] = "OM", ["।"] = "|", ["॥"] = "||",
            ["०"] = "0", ["१"] = "1", ["२"] = "2", ["३"] = "3", ["४"] = "4",
            ["५"] = "5", ["६"] = "6", ["७"] = "7", ["८"] = "8", ["९"] = "9",
        };

        private static readonly Dictionary<string, string> TeluguHK = new()
        {
            ["అ"] = "a", ["ఆ"] = "A", ["ఇ"] = "i", ["ఈ"] = "I", ["ఉ"] = "u", ["ఊ"] = "U",
            ["ఋ"] = "RRi", ["ౠ"] = "RRI", ["ఌ"] = "LLi", ["ౡ"] = "LLI",
            ["ఎ"] = "e", ["ఏ"] = "E", ["ఐ"] = "ai", ["ఒ"] = "o", ["ఓ"] = "O", ["ఔ"] = "au",
            ["క"] = "ka", ["ఖ"] = "kha", ["గ"] = "ga", ["ఘ"] = "gha", ["ఙ"] = "~Na",
            ["చ"] = "ca", ["ఛ"] = "cha", ["జ"] = "ja", ["ఝ"] = "jha", ["ఞ"] = "~na",
            ["ట"] = "Ta", ["ఠ"] = "Tha", ["డ"] = "Da", ["ఢ"] = "Dha", ["ణ"] = "Na",
            ["త"] = "ta", ["థ"] = "tha", ["ద"] = "da", ["ధ"] = "dha", ["న"] = "na",
            ["ప"] = "pa", ["ఫ"] = "pha", ["బ"] = "ba", ["భ"] = "bha", ["మ"] = "ma",
            ["య"] = "ya", ["ర"] = "ra", ["ల"] = "la", ["వ"] = "va",
            ["శ"] = "za", ["ష"] = "Sa", ["స"] = "sa", ["హ"] = "ha",
            ["ళ"] = "La", ["క్ష"] = "kSa", ["ఱ"] = "Ra",
            ["ా"] = "A", ["ి"] = "i", ["ీ"] = "I", ["ు"] = "u", ["ూ"] = "U",
            ["ృ"] = "RRi", ["ౄ"] = "RRI", ["ె"] = "e", ["ే"] = "E", ["ై"] = "ai",
            ["ొ"] = "o", ["ో"] = "O", ["ౌ"] = "au",
            ["ం"] = "M", ["ః"] = "H", ["ఁ"] = "M",
            ["్"] = "",
            ["౦"] = "0", ["౧"] = "1", ["౨"] = "2", ["౩"] = "3", ["౪"] = "4",
            ["౫"] = "5", ["౬"] = "6", ["౭"] = "7", ["౮"] = "8", ["౯"] = "9",
        };

        private static readonly Dictionary<string, string> TamilHK = new()
        {
            ["அ"] = "a", ["ஆ"] = "A", ["இ"] = "i", ["ஈ"] = "I", ["உ"] = "u", ["ஊ"] = "U",
            ["எ"] = "e", ["ஏ"] = "E", ["ஐ"] = "ai", ["ஒ"] = "o", ["ஓ"] = "O", ["ஔ"] = "au",
            ["க"] = "ka", ["ங"] = "~Na", ["ச"] = "ca", ["ஞ"] = "~na",
            ["ட"] = "Ta", ["ண"] = "Na", ["த"] = "ta", ["ந"] = "na",
            ["ப"] = "pa", ["ம"] = "ma", ["ய"] = "ya", ["ர"] = "ra", ["ல"] = "la", ["வ"] = "va",
            ["ழ"] = "La", ["ள"] = "La", ["ற"] = "Ra", ["ன"] = "na",
            ["ஶ"] = "za", ["ஷ"] = "Sa", ["ஸ"] = "sa", ["ஹ"] = "ha",
            ["ா"] = "A", ["ி"] = "i", ["ீ"] = "I", ["ு"] = "u", ["ூ"] = "U",
            ["ெ"] = "e", ["ே"] = "E", ["ை"] = "ai", ["ொ"] = "o", ["ோ"] = "O", ["ௌ"] = "au",
            ["ம்"] = "M", ["ஃ"] = "H",
            ["்"] = "",
            ["௦"] = "0", ["௧"] = "1", ["௨"] = "2", ["௩"] = "3", ["௪"] = "4",
            ["௫"] = "5", ["௬"] = "6", ["௭"] = "7", ["௮"] = "8", ["௯"] = "9",
        };

        private static readonly Dictionary<string, string> MalayalamHK = new()
        {
            ["അ"] = "a", ["ആ"] = "A", ["ഇ"] = "i", ["ഈ"] = "I", ["ഉ"] = "u", ["ഊ"] = "U",
            ["ഋ"] = "RRi", ["ൠ"] = "RRI", ["ഌ"] = "LLi", ["ൡ"] = "LLI",
            ["എ"] = "e", ["ഏ"] = "E", ["ഐ"] = "ai", ["ഒ"] = "o", ["ഓ"] = "O", ["ഔ"] = "au",
            ["ക"] = "ka", ["ഖ"] = "kha", ["ഗ"] = "ga", ["ഘ"] = "gha", ["ങ"] = "~Na",
            ["ച"] = "ca", ["ഛ"] = "cha", ["ജ"] = "ja", ["ഝ"] = "jha", ["ഞ"] = "~na",
            ["ട"] = "Ta", ["ഠ"] = "Tha", ["ഡ"] = "Da", ["ഢ"] = "Dha", ["ണ"] = "Na",
            ["ത"] = "ta", ["ഥ"] = "tha", ["ദ"] = "da", ["ധ"] = "dha", ["ന"] = "na",
            ["പ"] = "pa", ["ഫ"] = "pha", ["ബ"] = "ba", ["ഭ"] = "bha", ["മ"] = "ma",
            ["യ"] = "ya", ["ര"] = "ra", ["ല"] = "la", ["വ"] = "va",
            ["ശ"] = "za", ["ഷ"] = "Sa", ["സ"] = "sa", ["ഹ"] = "ha",
            ["ള"] = "La", ["ഴ"] = "La", ["റ"] = "Ra",
            ["ാ"] = "A", ["ി"] = "i", ["ീ"] = "I", ["ു"] = "u", ["ൂ"] = "U",
            ["ൃ"] = "RRi", ["െ"] = "e", ["േ"] = "E", ["ൈ"] = "ai",
            ["ൊ"] = "o", ["ോ"] = "O", ["ൌ"] = "au", ["ൗ"] = "au",
            ["ം"] = "M", ["ഃ"] = "H",
            ["്"] = "",
            ["൦"] = "0", ["൧"] = "1", ["൨"] = "2", ["൩"] = "3", ["൪"] = "4",
            ["൫"] = "5", ["൬"] = "6", ["൭"] = "7", ["൮"] = "8", ["൯"] = "9",
        };

        private static readonly Dictionary<string, string> KannadaHK = new()
        {
            ["ಅ"] = "a", ["ಆ"] = "A", ["ಇ"] = "i", ["ಈ"] = "I", ["ಉ"] = "u", ["ಊ"] = "U",
            ["ಋ"] = "RRi", ["ೠ"] = "RRI", ["ಌ"] = "LLi", ["ೡ"] = "LLI",
            ["ಎ"] = "e", ["ಏ"] = "E", ["ಐ"] = "ai", ["ಒ"] = "o", ["ಓ"] = "O", ["ಔ"] = "au",
            ["ಕ"] = "ka", ["ಖ"] = "kha", ["ಗ"] = "ga", ["ಘ"] = "gha", ["ಙ"] = "~Na",
            ["ಚ"] = "ca", ["ಛ"] = "cha", ["ಜ"] = "ja", ["ಝ"] = "jha", ["ಞ"] = "~na",
            ["ಟ"] = "Ta", ["ಠ"] = "Tha", ["ಡ"] = "Da", ["ಢ"] = "Dha", ["ಣ"] = "Na",
            ["ತ"] = "ta", ["ಥ"] = "tha", ["ದ"] = "da", ["ಧ"] = "dha", ["ನ"] = "na",
            ["ಪ"] = "pa", ["ಫ"] = "pha", ["ಬ"] = "ba", ["ಭ"] = "bha", ["ಮ"] = "ma",
            ["ಯ"] = "ya", ["ರ"] = "ra", ["ಲ"] = "la", ["ವ"] = "va",
            ["ಶ"] = "za", ["ಷ"] = "Sa", ["ಸ"] = "sa", ["ಹ"] = "ha",
            ["ಳ"] = "La", ["ಕ್ಷ"] = "kSa",
            ["ಾ"] = "A", ["ಿ"] = "i", ["ೀ"] = "I", ["ು"] = "u", ["ೂ"] = "U",
            ["ೃ"] = "RRi", ["ೆ"] = "e", ["ೇ"] = "E", ["ೈ"] = "ai",
            ["ೊ"] = "o", ["ೋ"] = "O", ["ೌ"] = "au",
            ["ಂ"] = "M", ["ಃ"] = "H",
            ["್"] = "",
            ["೦"] = "0", ["೧"] = "1", ["೨"] = "2", ["೩"] = "3", ["೪"] = "4",
            ["೫"] = "5", ["೬"] = "6", ["೭"] = "7", ["೮"] = "8", ["೯"] = "9",
        };

        private static string IndicToHK(string text, DetectedLang lang)
        {
            var map = lang switch
            {
                DetectedLang.Hindi or DetectedLang.Marathi or DetectedLang.Nepali => DevanagariHK,
                DetectedLang.Telugu => TeluguHK,
                DetectedLang.Tamil => TamilHK,
                DetectedLang.Malayalam => MalayalamHK,
                DetectedLang.Kannada => KannadaHK,
                _ => null
            };

            if (map == null) return text;

            var sb = new StringBuilder();
            foreach (char c in text)
            {
                var key = c.ToString();
                if (map.TryGetValue(key, out var hk))
                    sb.Append(hk);
                else
                    sb.Append(c.Unidecode());
            }

            return sb.ToString();
        }

        // ==================== PHONEME-BASED CROSS-SCRIPT TRANSLITERATION ====================
        //
        // Architecture: instead of a lossy Latin intermediate, we use a phoneme representation.
        // Each source language (Chinese, Japanese, Korean, Indic) produces a raw phoneme string
        // via RomanizeRaw(). Then comprehensive phoneme→target mapping tables render that into
        // the user's chosen target script — producing PURE native script output with no stray
        // English characters.
        //
        // Pipeline:
        //   Source text → Detect language → RomanizeRaw (phonemes) → PhonemeToTarget (target script)
        //
        // The mapping tables are built programmatically by combining base consonants with vowel
        // signs, plus CJK syllable overrides for Chinese/Japanese/Korean sources.

        /// <summary>
        /// Target script/language for custom transliteration.
        /// </summary>
        public enum TransliterationTarget
        {
            Latin,
            Devanagari,
            Telugu,
            Tamil,
            Malayalam,
            Kannada,
            Bengali,
            Gujarati,
            Gurmukhi,
            Odia,
            Sinhala,
            Thai,
            Myanmar,
            Khmer,
            Lao,
        }

        public static readonly Dictionary<TransliterationTarget, string> TargetLabels = new()
        {
            [TransliterationTarget.Latin] = "Latin (English)",
            [TransliterationTarget.Devanagari] = "Devanagari (हिन्दी)",
            [TransliterationTarget.Telugu] = "Telugu (తెలుగు)",
            [TransliterationTarget.Tamil] = "Tamil (தமிழ்)",
            [TransliterationTarget.Malayalam] = "Malayalam (മലയാളം)",
            [TransliterationTarget.Kannada] = "Kannada (ಕನ್ನಡ)",
            [TransliterationTarget.Bengali] = "Bengali (বাংলা)",
            [TransliterationTarget.Gujarati] = "Gujarati (ગુજરાતી)",
            [TransliterationTarget.Gurmukhi] = "Gurmukhi (ਪੰਜਾਬੀ)",
            [TransliterationTarget.Odia] = "Odia (ଓଡ଼ିଆ)",
            [TransliterationTarget.Sinhala] = "Sinhala (සිංහල)",
            [TransliterationTarget.Thai] = "Thai (ไทย)",
            [TransliterationTarget.Myanmar] = "Myanmar (မြန်မာ)",
            [TransliterationTarget.Khmer] = "Khmer (ភាសាខ្មែរ)",
            [TransliterationTarget.Lao] = "Lao (ລາວ)",
        };

        // ───── Phoneme→Target mapping tables (built at startup) ─────

        private static Dictionary<string, string>? _phonemeToDevanagari;
        private static Dictionary<string, string>? _phonemeToTelugu;
        private static Dictionary<string, string>? _phonemeToTamil;
        private static Dictionary<string, string>? _phonemeToMalayalam;
        private static Dictionary<string, string>? _phonemeToKannada;
        private static bool _phonemeMapsBuilt = false;

        /// <summary>
        /// Builds the phoneme→target mapping tables for a script from its forward HK map
        /// and additional CJK syllable overrides.
        ///
        /// The forward map has entries like: "క" → "ka" (script → phoneme).
        /// This method inverts them to "ka" → "క" (phoneme → script), then adds:
        ///   - All consonant+vowel combinations generated from base consonants
        ///   - CJK syllable overrides (pinyin, romaji, romanized)
        ///   - Standalone vowels
        /// </summary>
        private static Dictionary<string, string> BuildPhonemeMap(
            Dictionary<string, string> forwardMap,
            string[] vowelSigns,
            Dictionary<string, string>? cjkOverrides)
        {
            var map = new Dictionary<string, string>();

            // Step 1: Extract base consonants from the forward map.
            // A forward entry like "క" → "ka" means the base consonant "k" with inherent vowel "a".
            // We generate consonant+vowel combinations for all supported vowel signs.
            var consonantBases = new Dictionary<string, string>(); // phoneme base → script glyph (with inherent a stripped conceptually)

            foreach (var kvp in forwardMap)
            {
                var phoneme = kvp.Value; // e.g. "ka", "kha", "ga"
                var scriptChar = kvp.Key; // e.g. "క", "ఖ", "గ"

                if (string.IsNullOrEmpty(phoneme)) continue;

                // Store as-is: this maps the full consonant+vowel (usually "Xa") → script char
                map[phoneme] = scriptChar;

                // Extract base consonant by stripping the final "a" (inherent vowel)
                if (phoneme.EndsWith("a") && phoneme.Length > 1 && !"aeiou".Contains(phoneme[^2]))
                {
                    var baseConsonant = phoneme[..^1]; // "ka" → "k", "kha" → "kh"
                    if (!string.IsNullOrEmpty(baseConsonant))
                        consonantBases[baseConsonant] = scriptChar;
                }
            }

            // Step 2: Generate consonant+vowel combinations
            // Vowel signs are pairs of (phoneme vowel, script vowel sign or empty for inherent)
            // The inherent vowel "a" is already covered by the forward map entries above.
            // We add other vowels: A, i, I, u, U, e, ai, o, au, etc.
            foreach (var (basePhoneme, baseGlyph) in consonantBases)
            {
                foreach (var vs in vowelSigns)
                {
                    var parts = vs.Split('|', 2);
                    if (parts.Length != 2) continue;
                    var vowelPhoneme = parts[0];
                    var vowelSign = parts[1];

                    var combinedPhoneme = basePhoneme + vowelPhoneme;
                    var combinedGlyph = baseGlyph + vowelSign;

                    if (!map.ContainsKey(combinedPhoneme))
                        map[combinedPhoneme] = combinedGlyph;
                }
            }

            // Step 3: Add standalone vowels (extracted from forward map entries where the
            // key is a single character in the independent vowel range)
            // We already have these in the forward map as e.g. "అ" → "a"
            // These map individual Latin letters like "a", "A", "i", "I" to standalone vowels.

            // Step 4: Add CJK syllable overrides
            if (cjkOverrides != null)
            {
                foreach (var kvp in cjkOverrides)
                {
                    map[kvp.Key] = kvp.Value;
                }
            }



            return map;
        }

        /// <summary>
        /// Vowel sign definitions for each target script.
        /// Format: "phoneme|sign" where phoneme is the vowel sound and sign is the script diacritic.
        /// Empty string means inherent vowel (no visible sign).
        /// </summary>
        private static readonly string[] DevanagariVowelSigns = { "a|", "A|ा", "i|ि", "I|ी", "u|ु", "U|ू", "e|े", "ai|ै", "o|ो", "au|ौ" };
        private static readonly string[] TeluguVowelSigns = { "a|", "A|ా", "i|ి", "I|ీ", "u|ు", "U|ూ", "e|ె", "E|ే", "ai|ై", "o|ొ", "O|ో", "au|ౌ" };
        private static readonly string[] TamilVowelSigns = { "a|", "A|ா", "i|ி", "I|ீ", "u|ு", "U|ூ", "e|ெ", "E|ே", "ai|ை", "o|ொ", "O|ோ", "au|ௌ" };
        private static readonly string[] MalayalamVowelSigns = { "a|", "A|ാ", "i|ി", "I|ീ", "u|ു", "U|ൂ", "e|െ", "E|േ", "ai|ൈ", "o|ൊ", "O|ോ", "au|ൌ" };
        private static readonly string[] KannadaVowelSigns = { "a|", "A|ಾ", "i|ಿ", "I|ೀ", "u|ು", "U|ೂ", "e|ೆ", "E|ೇ", "ai|ೈ", "o|ೊ", "O|ೋ", "au|ೌ" };

        // ───── CJK syllable overrides: common pinyin syllables → target script approximation ─────
        // These ensure pure native output for Chinese/Japanese/Korean sources.

        private static readonly Dictionary<string, string> DevaCjkOverrides = new()
        {
            ["a"] = "अ",
            ["ai"] = "ै",
            ["an"] = "अन",
            ["ba"] = "ब",
            ["bai"] = "बि",
            ["ban"] = "बन",
            ["bao"] = "बु",
            ["bei"] = "बेि",
            ["ben"] = "बेन",
            ["bi"] = "बि",
            ["bian"] = "बयन",
            ["biao"] = "बयु",
            ["bie"] = "बये",
            ["bu"] = "बु",
            ["cao"] = "छु",
            ["chan"] = "छन",
            ["chang"] = "छनग",
            ["che"] = "चहे",
            ["cheng"] = "चहेनग",
            ["chi"] = "चहि",
            ["chu"] = "चहु",
            ["chun"] = "चहवन",
            ["ci"] = "चहि",
            ["cong"] = "चहोनग",
            ["da"] = "द",
            ["dan"] = "दन",
            ["dang"] = "दनग",
            ["dao"] = "दु",
            ["de"] = "दे",
            ["deng"] = "देनग",
            ["di"] = "दि",
            ["dian"] = "दयन",
            ["ding"] = "दिनग",
            ["dong"] = "दोनग",
            ["du"] = "दु",
            ["duan"] = "दवन",
            ["dui"] = "दवि",
            ["duo"] = "दवो",
            ["en"] = "ेन",
            ["er"] = "ेर",
            ["fa"] = "फ",
            ["fan"] = "फन",
            ["fang"] = "फनग",
            ["fen"] = "पहेन",
            ["feng"] = "पहेनग",
            ["fu"] = "पहु",
            ["gai"] = "गि",
            ["gan"] = "गन",
            ["gao"] = "गु",
            ["ge"] = "गे",
            ["gei"] = "गेि",
            ["gen"] = "गेन",
            ["geng"] = "गेनग",
            ["gong"] = "गोनग",
            ["guan"] = "गवन",
            ["guang"] = "गवनग",
            ["guo"] = "गवो",
            ["ha"] = "ह",
            ["hai"] = "हि",
            ["hao"] = "हु",
            ["he"] = "हे",
            ["hei"] = "हेि",
            ["hen"] = "हेन",
            ["hong"] = "होनग",
            ["hou"] = "होु",
            ["hua"] = "हव",
            ["huai"] = "हवि",
            ["huang"] = "हवनग",
            ["hui"] = "हवि",
            ["huo"] = "हवो",
            ["ji"] = "जि",
            ["jia"] = "जय",
            ["jian"] = "जयन",
            ["jiang"] = "जयनग",
            ["jiao"] = "जयु",
            ["jie"] = "जये",
            ["jin"] = "जिन",
            ["jing"] = "जिनग",
            ["jiu"] = "जयु",
            ["ju"] = "जु",
            ["jue"] = "जवे",
            ["jun"] = "जवन",
            ["kai"] = "कि",
            ["kan"] = "कन",
            ["ke"] = "के",
            ["kong"] = "कोनग",
            ["ku"] = "कु",
            ["kuai"] = "कवि",
            ["lai"] = "लि",
            ["lan"] = "लन",
            ["lao"] = "लु",
            ["le"] = "ले",
            ["leng"] = "लेनग",
            ["li"] = "लि",
            ["lian"] = "लयन",
            ["liang"] = "लयनग",
            ["ling"] = "लिनग",
            ["liu"] = "लयु",
            ["lu"] = "लु",
            ["lv"] = "लयु",
            ["ma"] = "म",
            ["mai"] = "मि",
            ["man"] = "मन",
            ["me"] = "मे",
            ["mei"] = "मेि",
            ["men"] = "मेन",
            ["meng"] = "मेनग",
            ["mian"] = "मयन",
            ["miao"] = "मयु",
            ["min"] = "मिन",
            ["ming"] = "मिनग",
            ["mu"] = "मु",
            ["na"] = "न",
            ["nan"] = "नन",
            ["ne"] = "ने",
            ["nei"] = "नेि",
            ["neng"] = "नेनग",
            ["ni"] = "नि",
            ["nian"] = "नयन",
            ["nu"] = "नु",
            ["nuan"] = "नवन",
            ["nv"] = "नयु",
            ["o"] = "ो",
            ["pao"] = "पु",
            ["peng"] = "पेनग",
            ["pian"] = "पयन",
            ["piao"] = "पयु",
            ["ping"] = "पिनग",
            ["qi"] = "चहि",
            ["qian"] = "चहयन",
            ["qie"] = "चहये",
            ["qin"] = "चहिन",
            ["qing"] = "चहिनग",
            ["qiu"] = "चहयु",
            ["qu"] = "चहु",
            ["quan"] = "चहवन",
            ["ran"] = "रन",
            ["rang"] = "रनग",
            ["re"] = "रे",
            ["ren"] = "रेन",
            ["ri"] = "रि",
            ["ru"] = "रु",
            ["san"] = "सन",
            ["shan"] = "सहन",
            ["shang"] = "सहनग",
            ["shao"] = "सहु",
            ["she"] = "सहे",
            ["shen"] = "सहेन",
            ["sheng"] = "सहेनग",
            ["shi"] = "सहि",
            ["shou"] = "सहोु",
            ["shu"] = "सहु",
            ["shuang"] = "सहवनग",
            ["shui"] = "सहवि",
            ["shuo"] = "सहवो",
            ["si"] = "सि",
            ["sui"] = "सवि",
            ["suo"] = "सवो",
            ["ta"] = "त",
            ["tai"] = "ति",
            ["te"] = "ते",
            ["ti"] = "ति",
            ["tian"] = "तयन",
            ["tiao"] = "तयु",
            ["ting"] = "तिनग",
            ["tong"] = "तोनग",
            ["tou"] = "तोु",
            ["wai"] = "वि",
            ["wan"] = "वन",
            ["wang"] = "वनग",
            ["wei"] = "वेि",
            ["wen"] = "वेन",
            ["wo"] = "वो",
            ["wu"] = "वु",
            ["xi"] = "सहि",
            ["xia"] = "सहय",
            ["xian"] = "सहयन",
            ["xiang"] = "सहयनग",
            ["xiao"] = "सहयु",
            ["xie"] = "सहये",
            ["xin"] = "सहिन",
            ["xing"] = "सहिनग",
            ["xu"] = "सहु",
            ["xue"] = "सहवे",
            ["yang"] = "यनग",
            ["yao"] = "यु",
            ["ye"] = "ये",
            ["yi"] = "यि",
            ["yin"] = "यिन",
            ["ying"] = "यिनग",
            ["yong"] = "योनग",
            ["you"] = "योु",
            ["yu"] = "यु",
            ["yuan"] = "यवन",
            ["yue"] = "यवे",
            ["yun"] = "यवन",
            ["zai"] = "शि",
            ["zao"] = "शु",
            ["zhan"] = "जन",
            ["zhang"] = "जनग",
            ["zhao"] = "जु",
            ["zhe"] = "जे",
            ["zhen"] = "जेन",
            ["zheng"] = "जेनग",
            ["zhi"] = "जि",
            ["zhong"] = "जोनग",
            ["zhu"] = "जु",
            ["zi"] = "शि",
            ["zong"] = "शोनग",
            ["zou"] = "शोु",
            ["zui"] = "शवि",
            ["zuo"] = "शवो",
        };

        private static readonly Dictionary<string, string> TeluguCjkOverrides = new()
        {
            ["a"] = "అ",
            ["ai"] = "ై",
            ["an"] = "అన",
            ["ba"] = "బ",
            ["bai"] = "బి",
            ["ban"] = "బన",
            ["bao"] = "బు",
            ["bei"] = "బెి",
            ["ben"] = "బెన",
            ["bi"] = "బి",
            ["bian"] = "బయన",
            ["biao"] = "బయు",
            ["bie"] = "బయె",
            ["bu"] = "బు",
            ["cao"] = "ఛు",
            ["chan"] = "ఛన",
            ["chang"] = "ఛనగ",
            ["che"] = "చహె",
            ["cheng"] = "చహెనగ",
            ["chi"] = "చహి",
            ["chu"] = "చహు",
            ["chun"] = "చహవన",
            ["ci"] = "చహి",
            ["cong"] = "చహొనగ",
            ["da"] = "ద",
            ["dan"] = "దన",
            ["dang"] = "దనగ",
            ["dao"] = "దు",
            ["de"] = "దె",
            ["deng"] = "దెనగ",
            ["di"] = "ది",
            ["dian"] = "దయన",
            ["ding"] = "దినగ",
            ["dong"] = "దొనగ",
            ["du"] = "దు",
            ["duan"] = "దవన",
            ["dui"] = "దవి",
            ["duo"] = "దవొ",
            ["en"] = "ెన",
            ["er"] = "ెర",
            ["fa"] = "ఫ",
            ["fan"] = "ఫన",
            ["fang"] = "ఫనగ",
            ["fen"] = "పహెన",
            ["feng"] = "పహెనగ",
            ["fu"] = "పహు",
            ["gai"] = "గి",
            ["gan"] = "గన",
            ["gao"] = "గు",
            ["ge"] = "గె",
            ["gei"] = "గెి",
            ["gen"] = "గెన",
            ["geng"] = "గెనగ",
            ["gong"] = "గొనగ",
            ["guan"] = "గవన",
            ["guang"] = "గవనగ",
            ["guo"] = "గవొ",
            ["ha"] = "హ",
            ["hai"] = "హి",
            ["hao"] = "హు",
            ["he"] = "హె",
            ["hei"] = "హెి",
            ["hen"] = "హెన",
            ["hong"] = "హొనగ",
            ["hou"] = "హొు",
            ["hua"] = "హవ",
            ["huai"] = "హవి",
            ["huang"] = "హవనగ",
            ["hui"] = "హవి",
            ["huo"] = "హవొ",
            ["ji"] = "జి",
            ["jia"] = "జయ",
            ["jian"] = "జయన",
            ["jiang"] = "జయనగ",
            ["jiao"] = "జయు",
            ["jie"] = "జయె",
            ["jin"] = "జిన",
            ["jing"] = "జినగ",
            ["jiu"] = "జయు",
            ["ju"] = "జు",
            ["jue"] = "జవె",
            ["jun"] = "జవన",
            ["kai"] = "కి",
            ["kan"] = "కన",
            ["ke"] = "కె",
            ["kong"] = "కొనగ",
            ["ku"] = "కు",
            ["kuai"] = "కవి",
            ["lai"] = "లి",
            ["lan"] = "లన",
            ["lao"] = "లు",
            ["le"] = "లె",
            ["leng"] = "లెనగ",
            ["li"] = "లి",
            ["lian"] = "లయన",
            ["liang"] = "లయనగ",
            ["ling"] = "లినగ",
            ["liu"] = "లయు",
            ["lu"] = "లు",
            ["lv"] = "లయు",
            ["ma"] = "మ",
            ["mai"] = "మి",
            ["man"] = "మన",
            ["me"] = "మె",
            ["mei"] = "మెి",
            ["men"] = "మెన",
            ["meng"] = "మెనగ",
            ["mian"] = "మయన",
            ["miao"] = "మయు",
            ["min"] = "మిన",
            ["ming"] = "మినగ",
            ["mu"] = "ము",
            ["na"] = "న",
            ["nan"] = "నన",
            ["ne"] = "నె",
            ["nei"] = "నెి",
            ["neng"] = "నెనగ",
            ["ni"] = "ని",
            ["nian"] = "నయన",
            ["nu"] = "ను",
            ["nuan"] = "నవన",
            ["nv"] = "నయు",
            ["o"] = "ొ",
            ["pao"] = "పు",
            ["peng"] = "పెనగ",
            ["pian"] = "పయన",
            ["piao"] = "పయు",
            ["ping"] = "పినగ",
            ["qi"] = "చహి",
            ["qian"] = "చహయన",
            ["qie"] = "చహయె",
            ["qin"] = "చహిన",
            ["qing"] = "చహినగ",
            ["qiu"] = "చహయు",
            ["qu"] = "చహు",
            ["quan"] = "చహవన",
            ["ran"] = "రన",
            ["rang"] = "రనగ",
            ["re"] = "రె",
            ["ren"] = "రెన",
            ["ri"] = "రి",
            ["ru"] = "రు",
            ["san"] = "సన",
            ["shan"] = "సహన",
            ["shang"] = "సహనగ",
            ["shao"] = "సహు",
            ["she"] = "సహె",
            ["shen"] = "సహెన",
            ["sheng"] = "సహెనగ",
            ["shi"] = "సహి",
            ["shou"] = "సహొు",
            ["shu"] = "సహు",
            ["shuang"] = "సహవనగ",
            ["shui"] = "సహవి",
            ["shuo"] = "సహవొ",
            ["si"] = "సి",
            ["sui"] = "సవి",
            ["suo"] = "సవొ",
            ["ta"] = "త",
            ["tai"] = "తి",
            ["te"] = "తె",
            ["ti"] = "తి",
            ["tian"] = "తయన",
            ["tiao"] = "తయు",
            ["ting"] = "తినగ",
            ["tong"] = "తొనగ",
            ["tou"] = "తొు",
            ["wai"] = "వి",
            ["wan"] = "వన",
            ["wang"] = "వనగ",
            ["wei"] = "వెి",
            ["wen"] = "వెన",
            ["wo"] = "వొ",
            ["wu"] = "వు",
            ["xi"] = "సహి",
            ["xia"] = "సహయ",
            ["xian"] = "సహయన",
            ["xiang"] = "సహయనగ",
            ["xiao"] = "సహయు",
            ["xie"] = "సహయె",
            ["xin"] = "సహిన",
            ["xing"] = "సహినగ",
            ["xu"] = "సహు",
            ["xue"] = "సహవె",
            ["yang"] = "యనగ",
            ["yao"] = "యు",
            ["ye"] = "యె",
            ["yi"] = "యి",
            ["yin"] = "యిన",
            ["ying"] = "యినగ",
            ["yong"] = "యొనగ",
            ["you"] = "యొు",
            ["yu"] = "యు",
            ["yuan"] = "యవన",
            ["yue"] = "యవె",
            ["yun"] = "యవన",
            ["zai"] = "శి",
            ["zao"] = "శు",
            ["zhan"] = "జన",
            ["zhang"] = "జనగ",
            ["zhao"] = "జు",
            ["zhe"] = "జె",
            ["zhen"] = "జెన",
            ["zheng"] = "జెనగ",
            ["zhi"] = "జి",
            ["zhong"] = "జొనగ",
            ["zhu"] = "జు",
            ["zi"] = "శి",
            ["zong"] = "శొనగ",
            ["zou"] = "శొు",
            ["zui"] = "శవి",
            ["zuo"] = "శవొ",
        };

        private static readonly Dictionary<string, string> TamilCjkOverrides = new()
        {
            ["a"] = "அ",
            ["ai"] = "ை",
            ["an"] = "அன",
            ["ba"] = "அ",
            ["bai"] = "ை",
            ["ban"] = "அன",
            ["bao"] = "ௌ",
            ["bei"] = "ெி",
            ["ben"] = "ென",
            ["bi"] = "ி",
            ["bian"] = "யன",
            ["biao"] = "யு",
            ["bie"] = "யெ",
            ["bu"] = "ு",
            ["cao"] = "சஹு",
            ["chan"] = "சஹன",
            ["chang"] = "சஹன",
            ["che"] = "சஹெ",
            ["cheng"] = "சஹென",
            ["chi"] = "சஹி",
            ["chu"] = "சஹு",
            ["chun"] = "சஹவன",
            ["ci"] = "சஹி",
            ["cong"] = "சஹொன",
            ["da"] = "அ",
            ["dan"] = "அன",
            ["dang"] = "அன",
            ["dao"] = "ௌ",
            ["de"] = "ெ",
            ["deng"] = "ென",
            ["di"] = "ி",
            ["dian"] = "யன",
            ["ding"] = "ின",
            ["dong"] = "ொன",
            ["du"] = "ு",
            ["duan"] = "வன",
            ["dui"] = "வி",
            ["duo"] = "வொ",
            ["en"] = "ென",
            ["er"] = "ெர",
            ["fa"] = "பஹ",
            ["fan"] = "பஹன",
            ["fang"] = "பஹன",
            ["fen"] = "பஹென",
            ["feng"] = "பஹென",
            ["fu"] = "பஹு",
            ["gai"] = "ை",
            ["gan"] = "அன",
            ["gao"] = "ௌ",
            ["ge"] = "ெ",
            ["gei"] = "ெி",
            ["gen"] = "ென",
            ["geng"] = "ென",
            ["gong"] = "ொன",
            ["guan"] = "வன",
            ["guang"] = "வன",
            ["guo"] = "வொ",
            ["ha"] = "ஹ",
            ["hai"] = "ஹி",
            ["hao"] = "ஹு",
            ["he"] = "ஹெ",
            ["hei"] = "ஹெி",
            ["hen"] = "ஹென",
            ["hong"] = "ஹொன",
            ["hou"] = "ஹொு",
            ["hua"] = "ஹவ",
            ["huai"] = "ஹவி",
            ["huang"] = "ஹவன",
            ["hui"] = "ஹவி",
            ["huo"] = "ஹவொ",
            ["ji"] = "ி",
            ["jia"] = "ய",
            ["jian"] = "யன",
            ["jiang"] = "யன",
            ["jiao"] = "யு",
            ["jie"] = "யெ",
            ["jin"] = "ின",
            ["jing"] = "ின",
            ["jiu"] = "யு",
            ["ju"] = "ு",
            ["jue"] = "வெ",
            ["jun"] = "வன",
            ["kai"] = "கி",
            ["kan"] = "கன",
            ["ke"] = "கெ",
            ["kong"] = "கொன",
            ["ku"] = "கு",
            ["kuai"] = "கவி",
            ["lai"] = "லி",
            ["lan"] = "லன",
            ["lao"] = "லு",
            ["le"] = "லெ",
            ["leng"] = "லென",
            ["li"] = "லி",
            ["lian"] = "லயன",
            ["liang"] = "லயன",
            ["ling"] = "லின",
            ["liu"] = "லயு",
            ["lu"] = "லு",
            ["lv"] = "லயு",
            ["ma"] = "ம",
            ["mai"] = "மி",
            ["man"] = "மன",
            ["me"] = "மெ",
            ["mei"] = "மெி",
            ["men"] = "மென",
            ["meng"] = "மென",
            ["mian"] = "மயன",
            ["miao"] = "மயு",
            ["min"] = "மின",
            ["ming"] = "மின",
            ["mu"] = "மு",
            ["na"] = "ன",
            ["nan"] = "னன",
            ["ne"] = "னெ",
            ["nei"] = "னெி",
            ["neng"] = "னென",
            ["ni"] = "னி",
            ["nian"] = "னயன",
            ["nu"] = "னு",
            ["nuan"] = "னவன",
            ["nv"] = "னயு",
            ["o"] = "ொ",
            ["pao"] = "பு",
            ["peng"] = "பென",
            ["pian"] = "பயன",
            ["piao"] = "பயு",
            ["ping"] = "பின",
            ["qi"] = "சஹி",
            ["qian"] = "சஹயன",
            ["qie"] = "சஹயெ",
            ["qin"] = "சஹின",
            ["qing"] = "சஹின",
            ["qiu"] = "சஹயு",
            ["qu"] = "சஹு",
            ["quan"] = "சஹவன",
            ["ran"] = "ரன",
            ["rang"] = "ரன",
            ["re"] = "ரெ",
            ["ren"] = "ரென",
            ["ri"] = "ரி",
            ["ru"] = "ரு",
            ["san"] = "ஸன",
            ["shan"] = "ஸஹன",
            ["shang"] = "ஸஹன",
            ["shao"] = "ஸஹு",
            ["she"] = "ஸஹெ",
            ["shen"] = "ஸஹென",
            ["sheng"] = "ஸஹென",
            ["shi"] = "ஸஹி",
            ["shou"] = "ஸஹொு",
            ["shu"] = "ஸஹு",
            ["shuang"] = "ஸஹவன",
            ["shui"] = "ஸஹவி",
            ["shuo"] = "ஸஹவொ",
            ["si"] = "ஸி",
            ["sui"] = "ஸவி",
            ["suo"] = "ஸவொ",
            ["ta"] = "த",
            ["tai"] = "தி",
            ["te"] = "தெ",
            ["ti"] = "தி",
            ["tian"] = "தயன",
            ["tiao"] = "தயு",
            ["ting"] = "தின",
            ["tong"] = "தொன",
            ["tou"] = "தொு",
            ["wai"] = "வி",
            ["wan"] = "வன",
            ["wang"] = "வன",
            ["wei"] = "வெி",
            ["wen"] = "வென",
            ["wo"] = "வொ",
            ["wu"] = "வு",
            ["xi"] = "ஸஹி",
            ["xia"] = "ஸஹய",
            ["xian"] = "ஸஹயன",
            ["xiang"] = "ஸஹயன",
            ["xiao"] = "ஸஹயு",
            ["xie"] = "ஸஹயெ",
            ["xin"] = "ஸஹின",
            ["xing"] = "ஸஹின",
            ["xu"] = "ஸஹு",
            ["xue"] = "ஸஹவெ",
            ["yang"] = "யன",
            ["yao"] = "யு",
            ["ye"] = "யெ",
            ["yi"] = "யி",
            ["yin"] = "யின",
            ["ying"] = "யின",
            ["yong"] = "யொன",
            ["you"] = "யொு",
            ["yu"] = "யு",
            ["yuan"] = "யவன",
            ["yue"] = "யவெ",
            ["yun"] = "யவன",
            ["zai"] = "ஶி",
            ["zao"] = "ஶு",
            ["zhan"] = "அன",
            ["zhang"] = "அன",
            ["zhao"] = "ௌ",
            ["zhe"] = "ெ",
            ["zhen"] = "ென",
            ["zheng"] = "ென",
            ["zhi"] = "ி",
            ["zhong"] = "ொன",
            ["zhu"] = "ு",
            ["zi"] = "ஶி",
            ["zong"] = "ஶொன",
            ["zou"] = "ஶொு",
            ["zui"] = "ஶவி",
            ["zuo"] = "ஶவொ",
        };

        private static readonly Dictionary<string, string> MalayalamCjkOverrides = new()
        {
            ["a"] = "അ",
            ["ai"] = "ൈ",
            ["an"] = "അന",
            ["ba"] = "ബ",
            ["bai"] = "ബി",
            ["ban"] = "ബന",
            ["bao"] = "ബു",
            ["bei"] = "ബെി",
            ["ben"] = "ബെന",
            ["bi"] = "ബി",
            ["bian"] = "ബയന",
            ["biao"] = "ബയു",
            ["bie"] = "ബയെ",
            ["bu"] = "ബു",
            ["cao"] = "ഛു",
            ["chan"] = "ഛന",
            ["chang"] = "ഛനഗ",
            ["che"] = "ചഹെ",
            ["cheng"] = "ചഹെനഗ",
            ["chi"] = "ചഹി",
            ["chu"] = "ചഹു",
            ["chun"] = "ചഹവന",
            ["ci"] = "ചഹി",
            ["cong"] = "ചഹൊനഗ",
            ["da"] = "ദ",
            ["dan"] = "ദന",
            ["dang"] = "ദനഗ",
            ["dao"] = "ദു",
            ["de"] = "ദെ",
            ["deng"] = "ദെനഗ",
            ["di"] = "ദി",
            ["dian"] = "ദയന",
            ["ding"] = "ദിനഗ",
            ["dong"] = "ദൊനഗ",
            ["du"] = "ദു",
            ["duan"] = "ദവന",
            ["dui"] = "ദവി",
            ["duo"] = "ദവൊ",
            ["en"] = "െന",
            ["er"] = "െര",
            ["fa"] = "ഫ",
            ["fan"] = "ഫന",
            ["fang"] = "ഫനഗ",
            ["fen"] = "പഹെന",
            ["feng"] = "പഹെനഗ",
            ["fu"] = "പഹു",
            ["gai"] = "ഗി",
            ["gan"] = "ഗന",
            ["gao"] = "ഗു",
            ["ge"] = "ഗെ",
            ["gei"] = "ഗെി",
            ["gen"] = "ഗെന",
            ["geng"] = "ഗെനഗ",
            ["gong"] = "ഗൊനഗ",
            ["guan"] = "ഗവന",
            ["guang"] = "ഗവനഗ",
            ["guo"] = "ഗവൊ",
            ["ha"] = "ഹ",
            ["hai"] = "ഹി",
            ["hao"] = "ഹു",
            ["he"] = "ഹെ",
            ["hei"] = "ഹെി",
            ["hen"] = "ഹെന",
            ["hong"] = "ഹൊനഗ",
            ["hou"] = "ഹൊു",
            ["hua"] = "ഹവ",
            ["huai"] = "ഹവി",
            ["huang"] = "ഹവനഗ",
            ["hui"] = "ഹവി",
            ["huo"] = "ഹവൊ",
            ["ji"] = "ജി",
            ["jia"] = "ജയ",
            ["jian"] = "ജയന",
            ["jiang"] = "ജയനഗ",
            ["jiao"] = "ജയു",
            ["jie"] = "ജയെ",
            ["jin"] = "ജിന",
            ["jing"] = "ജിനഗ",
            ["jiu"] = "ജയു",
            ["ju"] = "ജു",
            ["jue"] = "ജവെ",
            ["jun"] = "ജവന",
            ["kai"] = "കി",
            ["kan"] = "കന",
            ["ke"] = "കെ",
            ["kong"] = "കൊനഗ",
            ["ku"] = "കു",
            ["kuai"] = "കവി",
            ["lai"] = "ലി",
            ["lan"] = "ലന",
            ["lao"] = "ലു",
            ["le"] = "ലെ",
            ["leng"] = "ലെനഗ",
            ["li"] = "ലി",
            ["lian"] = "ലയന",
            ["liang"] = "ലയനഗ",
            ["ling"] = "ലിനഗ",
            ["liu"] = "ലയു",
            ["lu"] = "ലു",
            ["lv"] = "ലയു",
            ["ma"] = "മ",
            ["mai"] = "മി",
            ["man"] = "മന",
            ["me"] = "മെ",
            ["mei"] = "മെി",
            ["men"] = "മെന",
            ["meng"] = "മെനഗ",
            ["mian"] = "മയന",
            ["miao"] = "മയു",
            ["min"] = "മിന",
            ["ming"] = "മിനഗ",
            ["mu"] = "മു",
            ["na"] = "ന",
            ["nan"] = "നന",
            ["ne"] = "നെ",
            ["nei"] = "നെി",
            ["neng"] = "നെനഗ",
            ["ni"] = "നി",
            ["nian"] = "നയന",
            ["nu"] = "നു",
            ["nuan"] = "നവന",
            ["nv"] = "നയു",
            ["o"] = "ൊ",
            ["pao"] = "പു",
            ["peng"] = "പെനഗ",
            ["pian"] = "പയന",
            ["piao"] = "പയു",
            ["ping"] = "പിനഗ",
            ["qi"] = "ചഹി",
            ["qian"] = "ചഹയന",
            ["qie"] = "ചഹയെ",
            ["qin"] = "ചഹിന",
            ["qing"] = "ചഹിനഗ",
            ["qiu"] = "ചഹയു",
            ["qu"] = "ചഹു",
            ["quan"] = "ചഹവന",
            ["ran"] = "രന",
            ["rang"] = "രനഗ",
            ["re"] = "രെ",
            ["ren"] = "രെന",
            ["ri"] = "രി",
            ["ru"] = "രു",
            ["san"] = "സന",
            ["shan"] = "സഹന",
            ["shang"] = "സഹനഗ",
            ["shao"] = "സഹു",
            ["she"] = "സഹെ",
            ["shen"] = "സഹെന",
            ["sheng"] = "സഹെനഗ",
            ["shi"] = "സഹി",
            ["shou"] = "സഹൊു",
            ["shu"] = "സഹു",
            ["shuang"] = "സഹവനഗ",
            ["shui"] = "സഹവി",
            ["shuo"] = "സഹവൊ",
            ["si"] = "സി",
            ["sui"] = "സവി",
            ["suo"] = "സവൊ",
            ["ta"] = "ത",
            ["tai"] = "തി",
            ["te"] = "തെ",
            ["ti"] = "തി",
            ["tian"] = "തയന",
            ["tiao"] = "തയു",
            ["ting"] = "തിനഗ",
            ["tong"] = "തൊനഗ",
            ["tou"] = "തൊു",
            ["wai"] = "വി",
            ["wan"] = "വന",
            ["wang"] = "വനഗ",
            ["wei"] = "വെി",
            ["wen"] = "വെന",
            ["wo"] = "വൊ",
            ["wu"] = "വു",
            ["xi"] = "സഹി",
            ["xia"] = "സഹയ",
            ["xian"] = "സഹയന",
            ["xiang"] = "സഹയനഗ",
            ["xiao"] = "സഹയു",
            ["xie"] = "സഹയെ",
            ["xin"] = "സഹിന",
            ["xing"] = "സഹിനഗ",
            ["xu"] = "സഹു",
            ["xue"] = "സഹവെ",
            ["yang"] = "യനഗ",
            ["yao"] = "യു",
            ["ye"] = "യെ",
            ["yi"] = "യി",
            ["yin"] = "യിന",
            ["ying"] = "യിനഗ",
            ["yong"] = "യൊനഗ",
            ["you"] = "യൊു",
            ["yu"] = "യു",
            ["yuan"] = "യവന",
            ["yue"] = "യവെ",
            ["yun"] = "യവന",
            ["zai"] = "ശി",
            ["zao"] = "ശു",
            ["zhan"] = "ജന",
            ["zhang"] = "ജനഗ",
            ["zhao"] = "ജു",
            ["zhe"] = "ജെ",
            ["zhen"] = "ജെന",
            ["zheng"] = "ജെനഗ",
            ["zhi"] = "ജി",
            ["zhong"] = "ജൊനഗ",
            ["zhu"] = "ജു",
            ["zi"] = "ശി",
            ["zong"] = "ശൊനഗ",
            ["zou"] = "ശൊു",
            ["zui"] = "ശവി",
            ["zuo"] = "ശവൊ",
        };

        private static readonly Dictionary<string, string> KannadaCjkOverrides = new()
        {
            ["a"] = "ಅ",
            ["ai"] = "ೈ",
            ["an"] = "ಅನ",
            ["ba"] = "ಬ",
            ["bai"] = "ಬಿ",
            ["ban"] = "ಬನ",
            ["bao"] = "ಬು",
            ["bei"] = "ಬೆಿ",
            ["ben"] = "ಬೆನ",
            ["bi"] = "ಬಿ",
            ["bian"] = "ಬಯನ",
            ["biao"] = "ಬಯು",
            ["bie"] = "ಬಯೆ",
            ["bu"] = "ಬು",
            ["cao"] = "ಛು",
            ["chan"] = "ಛನ",
            ["chang"] = "ಛನಗ",
            ["che"] = "ಚಹೆ",
            ["cheng"] = "ಚಹೆನಗ",
            ["chi"] = "ಚಹಿ",
            ["chu"] = "ಚಹು",
            ["chun"] = "ಚಹವನ",
            ["ci"] = "ಚಹಿ",
            ["cong"] = "ಚಹೊನಗ",
            ["da"] = "ದ",
            ["dan"] = "ದನ",
            ["dang"] = "ದನಗ",
            ["dao"] = "ದು",
            ["de"] = "ದೆ",
            ["deng"] = "ದೆನಗ",
            ["di"] = "ದಿ",
            ["dian"] = "ದಯನ",
            ["ding"] = "ದಿನಗ",
            ["dong"] = "ದೊನಗ",
            ["du"] = "ದು",
            ["duan"] = "ದವನ",
            ["dui"] = "ದವಿ",
            ["duo"] = "ದವೊ",
            ["en"] = "ೆನ",
            ["er"] = "ೆರ",
            ["fa"] = "ಫ",
            ["fan"] = "ಫನ",
            ["fang"] = "ಫನಗ",
            ["fen"] = "ಪಹೆನ",
            ["feng"] = "ಪಹೆನಗ",
            ["fu"] = "ಪಹು",
            ["gai"] = "ಗಿ",
            ["gan"] = "ಗನ",
            ["gao"] = "ಗು",
            ["ge"] = "ಗೆ",
            ["gei"] = "ಗೆಿ",
            ["gen"] = "ಗೆನ",
            ["geng"] = "ಗೆನಗ",
            ["gong"] = "ಗೊನಗ",
            ["guan"] = "ಗವನ",
            ["guang"] = "ಗವನಗ",
            ["guo"] = "ಗವೊ",
            ["ha"] = "ಹ",
            ["hai"] = "ಹಿ",
            ["hao"] = "ಹು",
            ["he"] = "ಹೆ",
            ["hei"] = "ಹೆಿ",
            ["hen"] = "ಹೆನ",
            ["hong"] = "ಹೊನಗ",
            ["hou"] = "ಹೊು",
            ["hua"] = "ಹವ",
            ["huai"] = "ಹವಿ",
            ["huang"] = "ಹವನಗ",
            ["hui"] = "ಹವಿ",
            ["huo"] = "ಹವೊ",
            ["ji"] = "ಜಿ",
            ["jia"] = "ಜಯ",
            ["jian"] = "ಜಯನ",
            ["jiang"] = "ಜಯನಗ",
            ["jiao"] = "ಜಯು",
            ["jie"] = "ಜಯೆ",
            ["jin"] = "ಜಿನ",
            ["jing"] = "ಜಿನಗ",
            ["jiu"] = "ಜಯು",
            ["ju"] = "ಜು",
            ["jue"] = "ಜವೆ",
            ["jun"] = "ಜವನ",
            ["kai"] = "ಕಿ",
            ["kan"] = "ಕನ",
            ["ke"] = "ಕೆ",
            ["kong"] = "ಕೊನಗ",
            ["ku"] = "ಕು",
            ["kuai"] = "ಕವಿ",
            ["lai"] = "ಲಿ",
            ["lan"] = "ಲನ",
            ["lao"] = "ಲು",
            ["le"] = "ಲೆ",
            ["leng"] = "ಲೆನಗ",
            ["li"] = "ಲಿ",
            ["lian"] = "ಲಯನ",
            ["liang"] = "ಲಯನಗ",
            ["ling"] = "ಲಿನಗ",
            ["liu"] = "ಲಯು",
            ["lu"] = "ಲು",
            ["lv"] = "ಲಯು",
            ["ma"] = "ಮ",
            ["mai"] = "ಮಿ",
            ["man"] = "ಮನ",
            ["me"] = "ಮೆ",
            ["mei"] = "ಮೆಿ",
            ["men"] = "ಮೆನ",
            ["meng"] = "ಮೆನಗ",
            ["mian"] = "ಮಯನ",
            ["miao"] = "ಮಯು",
            ["min"] = "ಮಿನ",
            ["ming"] = "ಮಿನಗ",
            ["mu"] = "ಮು",
            ["na"] = "ನ",
            ["nan"] = "ನನ",
            ["ne"] = "ನೆ",
            ["nei"] = "ನೆಿ",
            ["neng"] = "ನೆನಗ",
            ["ni"] = "ನಿ",
            ["nian"] = "ನಯನ",
            ["nu"] = "ನು",
            ["nuan"] = "ನವನ",
            ["nv"] = "ನಯು",
            ["o"] = "ೊ",
            ["pao"] = "ಪು",
            ["peng"] = "ಪೆನಗ",
            ["pian"] = "ಪಯನ",
            ["piao"] = "ಪಯು",
            ["ping"] = "ಪಿನಗ",
            ["qi"] = "ಚಹಿ",
            ["qian"] = "ಚಹಯನ",
            ["qie"] = "ಚಹಯೆ",
            ["qin"] = "ಚಹಿನ",
            ["qing"] = "ಚಹಿನಗ",
            ["qiu"] = "ಚಹಯು",
            ["qu"] = "ಚಹು",
            ["quan"] = "ಚಹವನ",
            ["ran"] = "ರನ",
            ["rang"] = "ರನಗ",
            ["re"] = "ರೆ",
            ["ren"] = "ರೆನ",
            ["ri"] = "ರಿ",
            ["ru"] = "ರು",
            ["san"] = "ಸನ",
            ["shan"] = "ಸಹನ",
            ["shang"] = "ಸಹನಗ",
            ["shao"] = "ಸಹು",
            ["she"] = "ಸಹೆ",
            ["shen"] = "ಸಹೆನ",
            ["sheng"] = "ಸಹೆನಗ",
            ["shi"] = "ಸಹಿ",
            ["shou"] = "ಸಹೊು",
            ["shu"] = "ಸಹು",
            ["shuang"] = "ಸಹವನಗ",
            ["shui"] = "ಸಹವಿ",
            ["shuo"] = "ಸಹವೊ",
            ["si"] = "ಸಿ",
            ["sui"] = "ಸವಿ",
            ["suo"] = "ಸವೊ",
            ["ta"] = "ತ",
            ["tai"] = "ತಿ",
            ["te"] = "ತೆ",
            ["ti"] = "ತಿ",
            ["tian"] = "ತಯನ",
            ["tiao"] = "ತಯು",
            ["ting"] = "ತಿನಗ",
            ["tong"] = "ತೊನಗ",
            ["tou"] = "ತೊು",
            ["wai"] = "ವಿ",
            ["wan"] = "ವನ",
            ["wang"] = "ವನಗ",
            ["wei"] = "ವೆಿ",
            ["wen"] = "ವೆನ",
            ["wo"] = "ವೊ",
            ["wu"] = "ವು",
            ["xi"] = "ಸಹಿ",
            ["xia"] = "ಸಹಯ",
            ["xian"] = "ಸಹಯನ",
            ["xiang"] = "ಸಹಯನಗ",
            ["xiao"] = "ಸಹಯು",
            ["xie"] = "ಸಹಯೆ",
            ["xin"] = "ಸಹಿನ",
            ["xing"] = "ಸಹಿನಗ",
            ["xu"] = "ಸಹು",
            ["xue"] = "ಸಹವೆ",
            ["yang"] = "ಯನಗ",
            ["yao"] = "ಯು",
            ["ye"] = "ಯೆ",
            ["yi"] = "ಯಿ",
            ["yin"] = "ಯಿನ",
            ["ying"] = "ಯಿನಗ",
            ["yong"] = "ಯೊನಗ",
            ["you"] = "ಯೊು",
            ["yu"] = "ಯು",
            ["yuan"] = "ಯವನ",
            ["yue"] = "ಯವೆ",
            ["yun"] = "ಯವನ",
            ["zai"] = "ಶಿ",
            ["zao"] = "ಶು",
            ["zhan"] = "ಜನ",
            ["zhang"] = "ಜನಗ",
            ["zhao"] = "ಜು",
            ["zhe"] = "ಜೆ",
            ["zhen"] = "ಜೆನ",
            ["zheng"] = "ಜೆನಗ",
            ["zhi"] = "ಜಿ",
            ["zhong"] = "ಜೊನಗ",
            ["zhu"] = "ಜು",
            ["zi"] = "ಶಿ",
            ["zong"] = "ಶೊನಗ",
            ["zou"] = "ಶೊು",
            ["zui"] = "ಶವಿ",
            ["zuo"] = "ಶವೊ",
        };

        private static void EnsurePhonemeMaps()
        {
            if (_phonemeMapsBuilt) return;

            _phonemeToDevanagari = BuildPhonemeMap(DevanagariHK, DevanagariVowelSigns, DevaCjkOverrides);
            _phonemeToTelugu = BuildPhonemeMap(TeluguHK, TeluguVowelSigns, TeluguCjkOverrides);
            _phonemeToTamil = BuildPhonemeMap(TamilHK, TamilVowelSigns, TamilCjkOverrides);
            _phonemeToMalayalam = BuildPhonemeMap(MalayalamHK, MalayalamVowelSigns, MalayalamCjkOverrides);
            _phonemeToKannada = BuildPhonemeMap(KannadaHK, KannadaVowelSigns, KannadaCjkOverrides);

            _phonemeMapsBuilt = true;
            Console.WriteLine($"[Transliteration] Built phoneme maps: Deva={_phonemeToDevanagari.Count}, Tel={_phonemeToTelugu.Count}, Tam={_phonemeToTamil.Count}, Mal={_phonemeToMalayalam.Count}, Kan={_phonemeToKannada.Count} entries");
        }

        /// <summary>
        /// Renders a raw phoneme string to the target script using greedy longest-prefix matching.
        /// Unknown phonemes are passed through unchanged as a fallback.
        /// </summary>
        private static string PhonemeToScript(string phonemeStr, Dictionary<string, string> phonemeMap)
        {
            var sb = new StringBuilder();
            int i = 0;
            int maxLen = 10;

            while (i < phonemeStr.Length)
            {
                if (char.IsWhiteSpace(phonemeStr[i]))
                {
                    sb.Append(phonemeStr[i]);
                    i++;
                    continue;
                }

                string? match = null;
                int matchLen = 0;

                int searchEnd = Math.Min(i + maxLen, phonemeStr.Length);
                for (int len = searchEnd - i; len >= 1; len--)
                {
                    var sub = phonemeStr.Substring(i, len);
                    if (phonemeMap.TryGetValue(sub, out var val))
                    {
                        match = val;
                        matchLen = len;
                        break;
                    }
                }

                if (match != null)
                {
                    sb.Append(match);
                    i += matchLen;
                }
                else
                {
                    // Pass through unknown characters unchanged
                    sb.Append(phonemeStr[i]);
                    i++;
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// Romanizes text WITHOUT the destructive CleanPronunciation pass.
        /// This preserves the raw phoneme information needed for cross-script rendering.
        /// </summary>
        /// <summary>
        /// Checks if a detected language is supported by Aksharamukha for romanization or cross-script conversion.
        /// </summary>
        private static bool IsAksharaSource(DetectedLang lang) => lang switch
        {
            DetectedLang.Hindi or DetectedLang.Marathi or DetectedLang.Nepali or
            DetectedLang.Telugu or DetectedLang.Tamil or
            DetectedLang.Malayalam or DetectedLang.Kannada or
            DetectedLang.Bengali or DetectedLang.Gujarati or
            DetectedLang.Gurmukhi or DetectedLang.Odia or
            DetectedLang.Sinhala or DetectedLang.Thai or
            DetectedLang.Lao or DetectedLang.Tibetan or
            DetectedLang.Myanmar or DetectedLang.Khmer or
            DetectedLang.Latin => true,
            _ => false
        };

        private static string[] LatinTargetBatch(string[] texts)
        {
            // Quick check: if all texts are already ASCII, no Aksharamukha call needed
            if (texts.All(t => t != null && t.All(c => c <= 0x7F)))
                return texts.ToArray();

            var combined = string.Concat(texts.Where(t => !string.IsNullOrWhiteSpace(t)));
            var sourceLang = DetectLanguage(combined);
            if (IsAksharaSource(sourceLang))
            {
                var sourceScript = AksharamukhaService.DetectedLangToScriptName(sourceLang);
                var cleanedTexts = texts.Select(t => t ?? "").ToArray();
                return AksharamukhaService.BatchTransliterate(cleanedTexts, sourceScript, "ISO");
            }
            // Fall back to existing Romanize for CJK/other unsupported sources
            var results = new string[texts.Length];
            for (int i = 0; i < texts.Length; i++)
                results[i] = Romanize(texts[i] ?? "");
            return results;
        }

        private static string AksharaRomanize(string text, DetectedLang lang)
        {
            var sourceScript = AksharamukhaService.DetectedLangToScriptName(lang);
            var results = AksharamukhaService.BatchTransliterate(new[] { text }, sourceScript, "ISO");
            return results.Length > 0 ? results[0] : text;
        }

        private static string RomanizeRaw(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return text;

            bool allAscii = true;
            foreach (char c in text)
            {
                if (c > 0x7F) { allAscii = false; break; }
            }
            if (allAscii) return text;

            var lang = DetectLanguage(text);

            switch (lang)
            {
                case DetectedLang.Chinese:
                    return ChineseToPinyin(text);
                case DetectedLang.Japanese:
                    return JapaneseToRomaji(text);
                case DetectedLang.Korean:
                    return KoreanToRomanized(text);
                case DetectedLang.Hindi:
                case DetectedLang.Marathi:
                case DetectedLang.Nepali:
                case DetectedLang.Telugu:
                case DetectedLang.Tamil:
                case DetectedLang.Malayalam:
                case DetectedLang.Kannada:
                case DetectedLang.Bengali:
                case DetectedLang.Gujarati:
                case DetectedLang.Gurmukhi:
                case DetectedLang.Odia:
                case DetectedLang.Sinhala:
                case DetectedLang.Thai:
                case DetectedLang.Lao:
                case DetectedLang.Tibetan:
                case DetectedLang.Myanmar:
                case DetectedLang.Khmer:
                    return IndicToHK(text, lang);
                default:
                    return text.Unidecode();
            }
        }

        /// <summary>
        /// Converts text to a chosen target script using the phoneme pipeline.
        /// Flow: source text → detect language → phoneme string → target script.
        /// For the Latin target, falls back to the user-friendly <see cref="Romanize"/>.
        /// </summary>
        public static string ConvertToTarget(string text, TransliterationTarget target)
        {
            if (string.IsNullOrWhiteSpace(text))
                return text;

            if (target == TransliterationTarget.Latin)
                return Romanize(text);

            // Any non-Latin target → delegate to batch (single item)
            var results = ConvertToTargetBatch(new[] { text }, target);
            return results.Length > 0 ? results[0] : text;
        }

        // ==================== CLEANUP ====================

        private static readonly Dictionary<string, string> CleanupReplacements = new()
        {
            ["2"] = "",
            ["aa"] = "a", ["ii"] = "i", ["uu"] = "u", ["ee"] = "e", ["oo"] = "o",
            ["gh"] = "k", ["chh"] = "ch", ["dh"] = "d", ["th"] = "t", ["sh"] = "sh",
            ["mte"] = "nte", ["man"] = "maan",
            ["nnnn"] = "nn", ["nnn"] = "nn", ["lll"] = "ll", ["kkk"] = "kk",
            ["aA"] = "a", ["aM"] = "am", ["aH"] = "ah",
        };

        private static string CleanPronunciation(string text)
        {
            text = text.ToLowerInvariant();

            foreach (var kvp in CleanupReplacements)
            {
                text = text.Replace(kvp.Key, kvp.Value);
            }

            text = Regex.Replace(text, @"\s+", " ");
            return text.Trim();
        }

        // ==================== MAIN ENTRY POINT ====================

        /// <summary>
        /// Romanizes/transliterates text from any supported script to Latin characters.
        /// </summary>
        public static string Romanize(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return text;

            // Quick ASCII check — already romanized
            bool allAscii = true;
            foreach (char c in text)
            {
                if (c > 0x7F) { allAscii = false; break; }
            }
            if (allAscii) return text;

            string result;
            var lang = DetectLanguage(text);

            switch (lang)
            {
                case DetectedLang.Chinese:
                    result = ChineseToPinyin(text);
                    break;

                case DetectedLang.Japanese:
                    result = JapaneseToRomaji(text);
                    break;

                case DetectedLang.Korean:
                    result = KoreanToRomanized(text);
                    break;

                case DetectedLang.Hindi:
                case DetectedLang.Marathi:
                case DetectedLang.Nepali:
                case DetectedLang.Telugu:
                case DetectedLang.Tamil:
                case DetectedLang.Malayalam:
                case DetectedLang.Kannada:
                    result = IndicToHK(text, lang);
                    break;

                default:
                    result = text.Unidecode();
                    break;
            }

            return CleanPronunciation(result);
        }

        /// <summary>
        /// Batch transliterate an array of texts, using Aksharamukha for Indic→Indic
        /// conversions and falling back to per-item ConvertToTarget for CJK/Latin sources.
        /// </summary>
        public static string[] ConvertToTargetBatch(string[] texts, TransliterationTarget target)
        {
            if (texts == null || texts.Length == 0)
                return Array.Empty<string>();

            // Shortcut: Latin target → use per-item Romanize (manual HK/phoneme system)
            if (target == TransliterationTarget.Latin)
            {
                var results = new string[texts.Length];
                for (int i = 0; i < texts.Length; i++)
                    results[i] = Romanize(texts[i] ?? "");
                return results;
            }

            // Detect source language
            var combined = string.Concat(texts.Where(t => !string.IsNullOrWhiteSpace(t)));
            var sourceLang = DetectLanguage(combined);

            LogMsg($"ConvertToTargetBatch: target={target}, texts.Count={texts.Length}, firstText='{TruncateLog(texts[0], 50)}'");
            LogMsg($"ConvertToTargetBatch: combined len={combined.Length}, detected sourceLang={sourceLang}");

            // Route through Aksharamukha if source language is supported
            bool aksharaSupportsSource = IsAksharaSource(sourceLang);

            LogMsg($"ConvertToTargetBatch: aksharaSupportsSource={aksharaSupportsSource}");

            if (!aksharaSupportsSource)
            {
                LogMsg($"ConvertToTargetBatch: source not supported, falling back to Romanize");
                // Fall back to Latin (English) for unsupported source scripts
                var results = new string[texts.Length];
                for (int i = 0; i < texts.Length; i++)
                    results[i] = Romanize(texts[i] ?? "");
                return results;
            }

            // Use Aksharamukha for supported source scripts
            var sourceScript = AksharamukhaService.DetectedLangToScriptName(sourceLang);
            var targetScript = AksharamukhaService.TargetToScriptName(target);
            LogMsg($"ConvertToTargetBatch: calling Aksharamukha with sourceScript={sourceScript}, targetScript={targetScript}");
            var cleanedTexts = texts.Select(t => t ?? "").ToArray();
            return AksharamukhaService.BatchTransliterate(cleanedTexts, sourceScript, targetScript);
        }

        private static void LogMsg(string message)
        {
            try
            {
                var logPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TaskbarMusic", "translit_debug.log");
                var dir = Path.GetDirectoryName(logPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.AppendAllText(logPath,
                    $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} [TranslitService] {message}{Environment.NewLine}");
            }
            catch { }
        }

        private static string TruncateLog(string? s, int maxLen)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= maxLen ? s : s[..maxLen] + "...";
        }
    }
}
