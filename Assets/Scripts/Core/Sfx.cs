using UnityEngine;

namespace TideShot.Core
{
    /// <summary>
    /// 程序化音效合成。所有音效在启动时用波形直接算出来，不依赖任何音频素材文件。
    /// 为什么这样做：没有音频资源时，静音是「像原型」的第一大原因，
    /// 而合成音（噪声脉冲 + 扫频 + 琶音）虽然达不到配乐水准，
    /// 但足以建立「命中/击杀/受伤/升级」的听觉区分度。
    /// 正式版应替换为真实录制或采购的音效。
    /// @trace AUD-001
    /// </summary>
    public static class Sfx
    {
        private const int SampleRate = 44100;

        private static AudioSource _source;
        private static AudioClip _hitA;
        private static AudioClip _hitB;
        private static AudioClip _kill;
        private static AudioClip _hurt;
        private static AudioClip _levelUp;
        private static AudioClip _blackout;
        private static AudioClip _waveStart;
        private static AudioClip _pickup;
        private static bool _ready;

        private static float _lastHitTime;
        private static float _lastPickupTime;

        public static float MasterVolume = 0.45f;

        public static void Initialize(GameObject host)
        {
            if (_ready)
            {
                return;
            }

            _source = host.GetComponent<AudioSource>();
            if (_source == null)
            {
                _source = host.AddComponent<AudioSource>();
            }
            _source.playOnAwake = false;
            _source.spatialBlend = 0f;
            _source.volume = 1f;

            _hitA = Noise("sfx_hit_a", 0.055f, 3.2f, 0.55f, 0.75f);
            _hitB = Noise("sfx_hit_b", 0.06f, 3.0f, 0.4f, 0.7f);
            _kill = Sweep("sfx_kill", 0.16f, 620f, 130f, 2.4f, 0.6f);
            _hurt = Sweep("sfx_hurt", 0.26f, 150f, 62f, 2.0f, 0.85f);
            _blackout = Noise("sfx_blackout", 0.55f, 1.4f, 0.12f, 0.55f);
            _waveStart = Sweep("sfx_wave", 0.42f, 90f, 150f, 1.5f, 0.7f);
            _pickup = Sweep("sfx_pickup", 0.07f, 900f, 1400f, 2.5f, 0.3f);
            _levelUp = Arpeggio("sfx_levelup", 0.09f, new[] { 523.25f, 659.25f, 783.99f, 1046.5f }, 0.5f);

            _ready = true;
        }

        // ---------------- 对外接口 ----------------

        public static void Hit(bool crit)
        {
            // 同一帧多枚投射物命中时只播一次，避免爆音
            if (Time.unscaledTime - _lastHitTime < 0.025f)
            {
                return;
            }
            _lastHitTime = Time.unscaledTime;
            Play(crit ? _hitB : _hitA, crit ? 0.8f : 0.6f);
        }

        public static void Kill()
        {
            Play(_kill, 0.7f);
        }

        public static void Hurt()
        {
            Play(_hurt, 0.9f);
        }

        public static void Blackout()
        {
            Play(_blackout, 0.75f);
        }

        public static void WaveStart()
        {
            Play(_waveStart, 0.6f);
        }

        public static void LevelUp()
        {
            Play(_levelUp, 0.65f);
        }

        public static void Pickup()
        {
            if (Time.unscaledTime - _lastPickupTime < 0.05f)
            {
                return;
            }
            _lastPickupTime = Time.unscaledTime;
            Play(_pickup, 0.35f);
        }

        private static void Play(AudioClip clip, float volume)
        {
            if (!_ready || _source == null || clip == null)
            {
                return;
            }
            _source.PlayOneShot(clip, Mathf.Clamp01(volume * MasterVolume));
        }

        // ---------------- 波形合成 ----------------

        private static AudioClip Noise(string name, float duration, float decay, float smooth, float gain)
        {
            int n = Mathf.Max(64, Mathf.CeilToInt(SampleRate * duration));
            var buf = new float[n];
            float prev = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)(n - 1);
                float env = Mathf.Pow(1f - t, decay);
                float white = Random.Range(-1f, 1f);
                // 一阶低通：smooth 越小越闷
                prev += (white - prev) * Mathf.Clamp01(smooth);
                buf[i] = prev * env * gain;
            }
            return Make(name, buf);
        }

        private static AudioClip Sweep(string name, float duration, float f0, float f1, float decay, float gain)
        {
            int n = Mathf.Max(64, Mathf.CeilToInt(SampleRate * duration));
            var buf = new float[n];
            float phase = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)(n - 1);
                float f = Mathf.Lerp(f0, f1, t);
                phase += 2f * Mathf.PI * f / SampleRate;
                float env = Mathf.Pow(1f - t, decay);
                // 基频 + 少量二次谐波，比纯正弦更有「实体感」
                float s = Mathf.Sin(phase) * 0.8f + Mathf.Sin(phase * 2f) * 0.2f;
                buf[i] = s * env * gain;
            }
            return Make(name, buf);
        }

        private static AudioClip Arpeggio(string name, float noteDuration, float[] freqs, float gain)
        {
            int per = Mathf.Max(32, Mathf.CeilToInt(SampleRate * noteDuration));
            int n = per * freqs.Length;
            var buf = new float[n];
            for (int k = 0; k < freqs.Length; k++)
            {
                float phase = 0f;
                for (int i = 0; i < per; i++)
                {
                    float t = i / (float)per;
                    float env = Mathf.Pow(1f - t, 1.4f);
                    phase += 2f * Mathf.PI * freqs[k] / SampleRate;
                    buf[k * per + i] = Mathf.Sin(phase) * env * gain;
                }
            }
            return Make(name, buf);
        }

        private static AudioClip Make(string name, float[] data)
        {
            var clip = AudioClip.Create(name, data.Length, 1, SampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
