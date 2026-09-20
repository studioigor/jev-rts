using UnityEngine;

namespace Jev.Gameplay.Audio
{
    public enum GameSound { Click, Select, Gather, Build, Hit, Death, Order, Error, Victory, Chop, Mine }

    /// <summary>Short synthesized effects; the voice pool is serialized in the service prefab.</summary>
    [DisallowMultipleComponent]
    public sealed class ProceduralAudio : MonoBehaviour
    {
        [SerializeField] AudioSource[] voices;
        [SerializeField, Range(0, 1)] float volume = .35f;
        AudioClip[] clips;
        readonly float[] lastPlayed = new float[System.Enum.GetValues(typeof(GameSound)).Length];
        int voiceIndex;
        public static ProceduralAudio Instance { get; private set; }

        void Awake()
        {
            Instance = this;
            clips = new AudioClip[lastPlayed.Length];
            for (int i = 0; i < clips.Length; i++) { clips[i] = Synthesize((GameSound)i); lastPlayed[i] = -1; }
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (clips != null) foreach (var clip in clips) if (clip != null) Destroy(clip);
        }

        public void Play(GameSound sound, Vector3 position = default, bool spatial = false)
        {
            int index = (int)sound;
            if (voices == null || voices.Length == 0 || clips == null) return;
            if (Time.unscaledTime - lastPlayed[index] < (spatial ? .065f : .035f)) return;
            lastPlayed[index] = Time.unscaledTime;
            var source = voices[voiceIndex++ % voices.Length];
            if (source == null) return;
            source.transform.position = position;
            source.Stop();
            source.spatialBlend = spatial ? .7f : 0;
            source.volume = volume;
            source.pitch = spatial ? Random.Range(.94f, 1.06f) : 1;
            source.clip = clips[index];
            source.Play();
        }

        static AudioClip Synthesize(GameSound sound)
        {
            const int rate = 22050;
            float duration = sound == GameSound.Victory ? .7f : sound == GameSound.Death ? .35f : sound == GameSound.Chop || sound == GameSound.Mine ? .24f : .15f;
            float[] samples = new float[Mathf.CeilToInt(duration * rate)];
            uint noiseSeed = 101u + (uint)sound;
            float filter = 0;
            for (int i = 0; i < samples.Length; i++)
            {
                float t = i / (float)rate, u = t / duration;
                noiseSeed ^= noiseSeed << 13; noiseSeed ^= noiseSeed >> 17; noiseSeed ^= noiseSeed << 5;
                float noise = (noiseSeed & 65535) / 32767.5f - 1;
                filter = Mathf.Lerp(filter, noise, .18f);
                float wave;
                switch (sound)
                {
                    case GameSound.Select: wave = Mathf.Sin(t * 2 * Mathf.PI * 510) * .35f + Mathf.Sin(t * 2 * Mathf.PI * 765) * .13f; break;
                    case GameSound.Gather: wave = Mathf.Sin(t * 2 * Mathf.PI * 870) * .22f + noise * .45f; break;
                    case GameSound.Chop: wave = Mathf.Sin(t * 2 * Mathf.PI * (155 - 60 * u)) * .5f * Mathf.Exp(-14 * t) + noise * .7f * Mathf.Exp(-55 * t) + filter * .35f; break;
                    case GameSound.Mine: wave = (Mathf.Sin(t * 2 * Mathf.PI * 1320) * .26f + Mathf.Sin(t * 2 * Mathf.PI * 2171) * .17f) * Mathf.Exp(-8 * t) + noise * .55f * Mathf.Exp(-50 * t); break;
                    case GameSound.Build: wave = Mathf.Sin(t * 2 * Mathf.PI * (180 - 50 * u)) * .45f + filter * .4f; break;
                    case GameSound.Hit: wave = noise * .45f + Mathf.Sin(t * 2 * Mathf.PI * 90) * .45f; break;
                    case GameSound.Death: wave = filter * .7f + Mathf.Sin(t * 2 * Mathf.PI * (110 - 65 * u)) * .3f; break;
                    case GameSound.Order: wave = Mathf.Sin(t * 2 * Mathf.PI * (560 + 180 * u)) * .35f; break;
                    case GameSound.Error: wave = Mathf.Sin(t * 2 * Mathf.PI * 140) * .4f; break;
                    case GameSound.Victory: wave = (Mathf.Sin(t * 2 * Mathf.PI * 330) + Mathf.Sin(t * 2 * Mathf.PI * 415.3f) + Mathf.Sin(t * 2 * Mathf.PI * 494)) * .17f; break;
                    default: wave = Mathf.Sin(t * 2 * Mathf.PI * 680) * .25f + noise * .12f; break;
                }
                float envelope = Mathf.Min(t / .004f, 1) * Mathf.Pow(1 - u, 2.5f);
                samples[i] = wave * envelope;
            }
            var clip = AudioClip.Create("Synth " + sound, samples.Length, 1, rate, false);
            clip.SetData(samples, 0);
            return clip;
        }
    }
}
