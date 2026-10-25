using System;

namespace PlannerDNA.Core
{
    /// <summary>
    /// SplitMix64 pseudo-random generator. Used instead of System.Random because System.Random's
    /// algorithm is not specified across .NET implementations; this one is fully defined here
    /// (and trivially portable to Python), so a seed always reproduces the same city.
    /// </summary>
    public sealed class DeterministicRng
    {
        private ulong _state;

        public DeterministicRng(ulong seed)
        {
            _state = seed;
        }

        /// <summary>Derives an independent stream from a user seed and attempt number.</summary>
        public static DeterministicRng ForAttempt(int seed, int attempt)
        {
            unchecked
            {
                ulong s = (ulong)(uint)seed * 0x9E3779B97F4A7C15UL ^ ((ulong)(uint)attempt + 1UL) * 0xD1B54A32D192ED03UL;
                return new DeterministicRng(s);
            }
        }

        public ulong NextUInt64()
        {
            unchecked
            {
                _state += 0x9E3779B97F4A7C15UL;
                ulong z = _state;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }

        /// <summary>Uniform double in [0, 1) using the top 53 bits.</summary>
        public double NextDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));

        /// <summary>Uniform integer in [minInclusive, maxExclusive).</summary>
        public int Range(int minInclusive, int maxExclusive)
        {
            if (maxExclusive <= minInclusive) throw new ArgumentException("Empty range.");
            ulong span = (ulong)(maxExclusive - minInclusive);
            return minInclusive + (int)(NextUInt64() % span);
        }

        public bool Chance(double probability) => NextDouble() < probability;
    }
}
