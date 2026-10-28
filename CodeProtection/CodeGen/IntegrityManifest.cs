using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace CodeProtection
{
    /// Shared data model plus hashing and signing helpers for the Code Integrity
    /// Check. Used by the editor build step (to write the manifest) and by the
    /// runtime verifier (to check it). Keep this file identical on both sides: it
    /// is the contract between build and runtime.
    [Serializable]
    public sealed class IntegrityEntry
    {
        public string path;   // relative to Application.dataPath, forward slashes
        public string hash;   // lowercase hex SHA256 of the file bytes
    }

    [Serializable]
    public sealed class IntegrityManifest
    {
        public int version = 1;
        public bool quitOnTamper = true;
        public string signature;         // HMAC SHA256 over the canonical form
        public IntegrityEntry[] entries = Array.Empty<IntegrityEntry>();

        public const string FileName = "integrity.manifest.json";

        /// Deterministic string the signature is computed over.
        public string Canonical()
        {
            var list = new List<IntegrityEntry>(entries ?? Array.Empty<IntegrityEntry>());
            list.Sort((a, b) => string.CompareOrdinal(a.path, b.path));
            var sb = new StringBuilder();
            sb.Append(version).Append('|').Append(quitOnTamper ? '1' : '0').Append('\n');
            foreach (var e in list)
                sb.Append(e.path).Append('\t').Append(e.hash).Append('\n');
            return sb.ToString();
        }

        public void Sign(string secret) => signature = Hmac(secret, Canonical());

        public bool VerifySignature(string secret)
        {
            if (string.IsNullOrEmpty(signature)) return false;
            string expected = Hmac(secret, Canonical());
            // Constant time compare so a wrong signature does not leak position.
            if (expected.Length != signature.Length) return false;
            int diff = 0;
            for (int i = 0; i < expected.Length; i++) diff |= expected[i] ^ signature[i];
            return diff == 0;
        }

        // ---- primitives ----

        public static string Sha256File(string fullPath)
        {
            using (var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var sha = SHA256.Create())
                return Hex(sha.ComputeHash(fs));
        }

        public static string Hmac(string secret, string data)
        {
            using (var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret ?? "")))
                return Hex(h.ComputeHash(Encoding.UTF8.GetBytes(data)));
        }

        static string Hex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}
