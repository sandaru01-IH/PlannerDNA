using System.Text;

namespace PlannerDNA.Core
{
    /// <summary>
    /// 64-bit FNV-1a. Unlike string.GetHashCode it is identical across runs, machines and runtimes.
    /// </summary>
    public sealed class StableHash
    {
        private const ulong OffsetBasis = 14695981039346656037UL;
        private const ulong Prime = 1099511628211UL;
        private ulong _value = OffsetBasis;

        public StableHash Add(int value)
        {
            unchecked
            {
                uint v = (uint)value;
                for (int i = 0; i < 4; i++)
                {
                    _value ^= (byte)(v >> (8 * i));
                    _value *= Prime;
                }
            }
            return this;
        }

        public StableHash Add(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            Add(bytes.Length);
            unchecked
            {
                foreach (byte b in bytes)
                {
                    _value ^= b;
                    _value *= Prime;
                }
            }
            return this;
        }

        public ulong Value => _value;
        public string ToHex() => _value.ToString("x16");
    }
}
