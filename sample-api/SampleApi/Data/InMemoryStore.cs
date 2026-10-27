using System.Collections.Concurrent;
using SampleApi.Entities;

namespace SampleApi.Data;

public sealed class InMemoryStore<T> where T : class, IEntity
{
    private readonly ConcurrentDictionary<int, T> _items = new();
    private int _lastId;

    public IReadOnlyList<T> GetAll() => _items.Values.OrderBy(x => x.Id).ToList();

    public T? Get(int id) => _items.GetValueOrDefault(id);

    public T Add(T item)
    {
        item.Id = Interlocked.Increment(ref _lastId);
        _items[item.Id] = item;
        return item;
    }

    public bool Remove(int id) => _items.TryRemove(id, out _);
}
