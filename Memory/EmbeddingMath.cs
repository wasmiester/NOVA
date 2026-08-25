using System;
using System.Linq;
using System.Threading.Tasks;
using ElBruno.LocalEmbeddings;

namespace Nova;

// Shared embedding/similarity math for both MemoryStore (durable facts) and
// ConversationArchive (past-task summaries) - the exact same vector-to-bytes
// packing scheme and cosine-similarity formula, since both stores use the
// same LocalEmbeddingGenerator and need identical retrieval quality. Was
// previously duplicated verbatim in both files - a future change to either
// (a different embedding model with a new vector layout, a similarity-math
// fix) had no way to stay in sync between the two copies.
internal static class EmbeddingMath
{
    public static async Task<byte[]> EmbedToBytesAsync(LocalEmbeddingGenerator embeddingGenerator, string text)
    {
        float[] vector = (await embeddingGenerator.GenerateEmbeddingAsync(text)).Vector.ToArray();
        byte[] bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public static float[] BytesToFloats(byte[] bytes)
    {
        float[] vector = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, vector, 0, bytes.Length);
        return vector;
    }

    public static float CosineSimilarity(float[] a, float[] b)
    {
        float dot = 0f;
        float normA = 0f;
        float normB = 0f;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        return dot / (MathF.Sqrt(normA) * MathF.Sqrt(normB));
    }
}
