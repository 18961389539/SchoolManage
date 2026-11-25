using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace School.Training;

public sealed record ImportCredential(string Account, string Password);

public sealed class ImportPasswordReceipts
{
    sealed record Receipt(string OwnerId, DateTime ExpiresAt, ImportCredential[] Items);
    readonly ConcurrentDictionary<string, Receipt> receipts = new(StringComparer.Ordinal);

    public static string GeneratePassword()
        => "A1" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(9)).TrimEnd('=').Replace('+', 'x').Replace('/', 'y');

    public void Put(string batchId, string ownerId, ImportCredential[] items, DateTime expiresAt)
    {
        Purge();
        receipts[batchId] = new Receipt(ownerId, expiresAt, items);
    }

    public ImportCredential[]? Take(string batchId, string ownerId)
    {
        if (!receipts.TryGetValue(batchId, out var receipt)) return null;
        if (receipt.OwnerId != ownerId) return null;
        if (receipt.ExpiresAt <= DateTime.UtcNow)
        {
            receipts.TryRemove(batchId, out _);
            return null;
        }
        return receipts.TryRemove(new KeyValuePair<string, Receipt>(batchId, receipt)) ? receipt.Items : null;
    }

    void Purge()
    {
        var now = DateTime.UtcNow;
        foreach (var item in receipts.Where(item => item.Value.ExpiresAt <= now))
            receipts.TryRemove(item.Key, out _);
    }
}