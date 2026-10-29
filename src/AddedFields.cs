using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Oxide.Core
{
    public static class AddedFields
    {
        private static readonly ConcurrentDictionary<string, object> fields = new ConcurrentDictionary<string, object>();

        public static T Get<T>(object owner, string field) =>
            fields.TryGetValue(field, out object? table) && ((ConditionalWeakTable<object, StrongBox<T>>)table).TryGetValue(owner, out StrongBox<T>? box)
                ? box.Value!
                : default!;

        public static void Set<T>(object owner, T value, string field)
        {
            ConditionalWeakTable<object, StrongBox<T>> table =
                (ConditionalWeakTable<object, StrongBox<T>>)fields.GetOrAdd(field, _ => new ConditionalWeakTable<object, StrongBox<T>>());
            table.GetValue(owner, _ => new StrongBox<T>()).Value = value;
        }
    }
}
