using System.Text;

namespace CodeProtection
{

    /// Deterministic keystream shared by the C# encryptor and the C++ runtime
    /// decryptor. The exact same algorithm is reimplemented in
    /// Native/metadata_decrypt_patch.cpp. If you change one, change both.
    ///
    /// It uses a 64 bit FNV 1a seed expanded with an xorshift64* generator. This
    /// is stronger than a plain 32 bit xorshift but it is still a stream cipher
    /// whose key ships inside the game, so it is obfuscation, not cryptography.
    /// Its job is to make global-metadata.dat unreadable to casual rippers
    /// (Il2CppDumper, static analysis), not to stop a resourced attacker. If you
    /// need real strength, replace both sides with AES or ChaCha20 and keep them
    /// byte for byte identical.

    public static class XorKeystream
    {
        // FNV 1a 64 bit constants.
        const ulong FnvOffset = 14695981039346656037UL;
        const ulong FnvPrime = 1099511628211UL;

        public static ulong SeedFromPassphrase(string passphrase)
        {
            ulong hash = FnvOffset;
            byte[] bytes = Encoding.UTF8.GetBytes(passphrase ?? "");
            for (int i = 0; i < bytes.Length; i++)
            {
                hash ^= bytes[i];
                hash *= FnvPrime;
            }
            // Avoid a zero state (xorshift stays stuck at 0).
            return hash == 0UL ? 0x9E3779B97F4A7C15UL : hash;
        }

        /// XOR the buffer in place with the keystream for the given seed.
        public static void Apply(byte[] data, ulong seed)
        {
            ulong state = seed;
            for (int i = 0; i < data.Length; i++)
            {
                // xorshift64*
                state ^= state >> 12;
                state ^= state << 25;
                state ^= state >> 27;
                ulong x = state * 0x2545F4914F6CDD1DUL;
                data[i] ^= (byte)(x >> 56);
            }
        }
    }
}
