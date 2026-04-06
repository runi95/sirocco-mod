using System.Collections.Generic;
using System.Reflection;

namespace SiroccoMod.Helpers
{
    public static class IL2CppArrayHelper
    {
        public static int GetLength(object? collection)
        {
            if (collection == null) return 0;
            var prop = collection.GetType().GetProperty("Length")
                    ?? collection.GetType().GetProperty("Count");
            return (int)(prop?.GetValue(collection) ?? 0);
        }

        public static PropertyInfo? GetItemProperty(object collection)
        {
            return collection.GetType().GetProperty("Item");
        }

        public static IEnumerable<object> Iterate(object collection)
        {
            var itemProp = GetItemProperty(collection);
            int count = GetLength(collection);

            for (int i = 0; i < count; i++)
            {
                object? item = null;
                try { item = itemProp?.GetValue(collection, [i]); } catch { }
                if (item != null) yield return item;
            }
        }
    }
}
