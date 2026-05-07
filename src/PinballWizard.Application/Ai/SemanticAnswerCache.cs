using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;

namespace PinballWizard.Application.Ai;

// In-process LRU cache for full WizardAnswer values keyed by
// (normalized_question + prompt_version) per ADR-0015. Implementation
// uses a ConcurrentDictionary for the lookup map plus a
// ConcurrentLinkedList-equivalent (LinkedList under a lock) for LRU
// ordering. Capacity is bounded by AiFoundryOptions.SemanticCacheMaxEntries.
//
// Keys are SHA-256 hex of UTF-8(normalized_question + "::" + prompt_version)
// so the key is constant-size regardless of question length and the
// "::" separator avoids collision between e.g. (q="abc", v="def") and
// (q="abcdef", v=""). Truncated to first 32 chars (128-bit) — well below
// the birthday-collision floor at 512 entries.
public sealed class SemanticAnswerCache : ISemanticAnswerCache
{
    private readonly int _maxEntries;
    private readonly ConcurrentDictionary<string, LinkedListNode<CacheEntry>> _map;
    private readonly LinkedList<CacheEntry> _lru;
    private readonly Lock _lruLock;

    public SemanticAnswerCache(IOptions<AiFoundryOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _maxEntries = Math.Max(0, options.Value.SemanticCacheMaxEntries);
        _map = new ConcurrentDictionary<string, LinkedListNode<CacheEntry>>(StringComparer.Ordinal);
        _lru = new LinkedList<CacheEntry>();
        _lruLock = new Lock();
    }

    public bool TryGet(string normalizedQuestion, string promptVersion, out WizardAnswer answer)
    {
        if (_maxEntries == 0)
        {
            answer = null!;
            return false;
        }

        var key = ComputeKey(normalizedQuestion, promptVersion);
        if (!_map.TryGetValue(key, out var node))
        {
            answer = null!;
            return false;
        }

        // Move to head of LRU on access. Other readers/writers can race
        // around this lock — at worst the LRU ordering is approximate
        // under contention, which is acceptable.
        lock (_lruLock)
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
        }

        answer = node.Value.Answer;
        return true;
    }

    public void Store(string normalizedQuestion, string promptVersion, WizardAnswer answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        if (_maxEntries == 0)
        {
            return;
        }

        var key = ComputeKey(normalizedQuestion, promptVersion);
        var entry = new CacheEntry(key, answer);
        var node = new LinkedListNode<CacheEntry>(entry);

        lock (_lruLock)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                _lru.Remove(existing);
            }

            _lru.AddFirst(node);
            _map[key] = node;

            while (_lru.Count > _maxEntries)
            {
                var evict = _lru.Last;
                if (evict is null)
                {
                    break;
                }
                _lru.RemoveLast();
                _map.TryRemove(evict.Value.Key, out _);
            }
        }
    }

    public int Count => _map.Count;

    private static string ComputeKey(string normalizedQuestion, string promptVersion)
    {
        var bytes = Encoding.UTF8.GetBytes($"{normalizedQuestion}::{promptVersion}");
        var hash = SHA256.HashData(bytes);
        var sb = new StringBuilder(32);
        for (var i = 0; i < 16; i++)
        {
            sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    private sealed record CacheEntry(string Key, WizardAnswer Answer);
}
