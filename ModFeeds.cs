// ModFeeds.cs (Revision 2)
// Find and read the data other Railroader mods share, and share your own. Every mod that carries this file uses one
// shared list of feeds in the game, so a feed any mod publishes can be found and read by every other mod.
//
// USE
//   1. Copy this file into your mod. Change only the namespace line.
//   2. Read:    foreach (ModFeeds.FeedInfo f in ModFeeds.List()) { ... }
//               object answer = ModFeeds.Read("cars-waiting");
//   3. Publish: ModFeeds.Publish("my-feed", MyFeed.Read, "My data", "My Mod", "1.0.0", 1, fields) when your mod is
//      switched on, and ModFeeds.Withdraw("my-feed") when it is switched off. Call both, and Read, on the game's main
//      thread.
//
// RULES (for a feed you publish)
//   - Your read function returns plain data only: Dictionary<string, object>, List<object>, string, bool, int, long,
//     float, double or null. Start the answer with "api" (int, your schema version) and "feed" (string, the id). Use
//     camelCase keys. No NaN or infinity.
//   - It never throws, changes nothing, and is cheap enough to call every couple of seconds. It runs on the game's main
//     thread.
//   - No file paths, account names or machine names in an answer or in the description you publish.
//   - Describe every top-level field in `fields`, units included, so anyone who finds the feed knows what it means.
//   - If you change this file, bump Revision and keep the shared contract below exactly: it is what lets copies of any
//     revision, from any author, work together.
//
// BEHAVIOR
//   - A mod is its assembly. The first mod to publish an id keeps it; another mod publishing the same id gets false.
//     Publishing again from the same mod (any copy of this file in that assembly) replaces its own entry.
//   - Withdraw removes only your own entry.
//   - Read itself never throws, even when a feed does: it answers { feed, error } instead. What a feed returns is the
//     publisher's data, handed over as is: treat it as read only, and as possibly large or deeply nested.
//   - Nothing runs on its own. Work happens only when a mod calls in.
//   - It names no game type, so it is safe to call from any lifecycle method.
//
// THE SHARED CONTRACT (frozen: every copy of this file, of any revision, keeps it exactly)
//   - AppDomain.CurrentDomain.GetData("ashenmare.feeds.v1") is a Dictionary<string, Dictionary<string, object>>, keyed
//     by feed id (lowercase letters and digits in words joined by '-', at most 64 characters). The first copy that
//     needs it creates it, holding lock (AppDomain.CurrentDomain).
//   - Every read and write of the dictionary holds lock (the dictionary).
//   - Each entry: "read" Func<object>; "title", "mod", "version" string; "api" int;
//     "fields" Dictionary<string, string>; "owner" string (the publishing assembly's simple name).
//   - A copy adds, replaces or removes only entries whose "owner" is its own assembly's name. Entries are replaced
//     whole, never changed in place.
//   - Readers ignore keys they don't know and treat a missing or mistyped key as absent, so a later revision can add
//     keys without breaking an earlier one.
//   - The dictionaries use ordinal string keys. Every call runs on the game's main thread.
//   - Nothing else crosses between mods.

using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace MODFEEDS_NAMESPACE
{
    internal static class ModFeeds
    {
        internal const int Revision = 2;

        private const string SlotKey = "ashenmare.feeds.v1";
        private const int MaxIdLength = 64;

        // This mod's name as an owner: every entry it publishes carries it, and it changes only those.
        private static readonly string Owner = OwnerName();

        private static string OwnerName()
        {
            try { return typeof(ModFeeds).Assembly.GetName().Name ?? ""; }
            catch { return ""; }
        }

        // What List gives back: a feed's description, copied, so nothing a caller does reaches the shared list.
        internal sealed class FeedInfo
        {
            internal readonly string Id, Title, Mod, Version;
            internal readonly int Api;
            internal readonly IDictionary<string, string> Fields;

            internal FeedInfo(string id, string title, string mod, string version, int api,
                              IDictionary<string, string> fields)
            {
                Id = id; Title = title; Mod = mod; Version = version; Api = api; Fields = fields;
            }
        }

        // Adds this mod's feed. False when the id is invalid, read is null, the id belongs to another mod, or the
        // shared list can't be reached.
        internal static bool Publish(string id, Func<object> read, string title, string mod, string version, int api,
                                     IDictionary<string, string> fields)
        {
            try
            {
                if (!ValidId(id) || read == null || Owner.Length == 0) return false;
                Dictionary<string, Dictionary<string, object>> slot = Slot(true);
                if (slot == null) return false;
                var copy = new Dictionary<string, string>(StringComparer.Ordinal);
                if (fields != null)
                    foreach (KeyValuePair<string, string> f in fields)
                        if (f.Key != null) copy[f.Key] = f.Value ?? "";
                var entry = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "read", read }, { "title", title ?? id }, { "mod", mod ?? "" }, { "version", version ?? "" },
                    { "api", api }, { "fields", copy }, { "owner", Owner },
                };
                lock (slot)
                {
                    if (slot.TryGetValue(id, out Dictionary<string, object> held) && !IsMine(held)) return false;
                    slot[id] = entry;
                }
                return true;
            }
            catch { return false; }
        }

        // Removes this mod's feed. Another mod's entry with the same id is left alone.
        internal static void Withdraw(string id)
        {
            try
            {
                Dictionary<string, Dictionary<string, object>> slot = Slot(false);
                if (slot == null || id == null) return;
                lock (slot)
                {
                    if (slot.TryGetValue(id, out Dictionary<string, object> held) && IsMine(held)) slot.Remove(id);
                }
            }
            catch { }
        }

        // Every feed loaded right now, sorted by id. Never null.
        internal static List<FeedInfo> List()
        {
            var list = new List<FeedInfo>();
            try
            {
                Dictionary<string, Dictionary<string, object>> slot = Slot(false);
                if (slot == null) return list;
                lock (slot)
                {
                    foreach (KeyValuePair<string, Dictionary<string, object>> kv in slot)
                    {
                        Dictionary<string, object> e = kv.Value;
                        if (e == null) continue;
                        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
                        if (Get(e, "fields") is Dictionary<string, string> f)
                            foreach (KeyValuePair<string, string> p in f) fields[p.Key] = p.Value;
                        list.Add(new FeedInfo(kv.Key, Get(e, "title") as string ?? kv.Key,
                                              Get(e, "mod") as string ?? "", Get(e, "version") as string ?? "",
                                              Get(e, "api") is int a ? a : 0, fields));
                    }
                }
                list.Sort((x, y) => string.CompareOrdinal(x.Id, y.Id));
            }
            catch { }
            return list;
        }

        // A feed's answer, or { feed, error } when it isn't loaded or its read threw. Never throws.
        internal static object Read(string id) => Read(id, out double _);

        // The same, with how long the feed's read took, in milliseconds (0 when it wasn't called).
        internal static object Read(string id, out double milliseconds)
        {
            milliseconds = 0;
            Func<object> read = null;
            try
            {
                Dictionary<string, Dictionary<string, object>> slot = Slot(false);
                if (slot != null)
                    lock (slot)
                        if (id != null && slot.TryGetValue(id, out Dictionary<string, object> e) && e != null)
                            read = Get(e, "read") as Func<object>;
            }
            catch { }
            if (read == null) return Error(id, "not loaded");
            Stopwatch clock = Stopwatch.StartNew();
            try { return read(); }
            catch (Exception e) { return Error(id, "the feed threw " + e.GetType().Name); }
            finally { milliseconds = clock.Elapsed.TotalMilliseconds; }
        }

        private static Dictionary<string, object> Error(string id, string why) =>
            new Dictionary<string, object>(StringComparer.Ordinal) { { "feed", id ?? "" }, { "error", why } };

        private static bool IsMine(Dictionary<string, object> entry) =>
            entry != null && Owner.Length > 0 && Get(entry, "owner") is string o
            && string.Equals(o, Owner, StringComparison.Ordinal);

        private static object Get(Dictionary<string, object> entry, string key) =>
            entry.TryGetValue(key, out object v) ? v : null;

        // The shared list, created on first need when create is true. Null when the slot holds something else.
        private static Dictionary<string, Dictionary<string, object>> Slot(bool create)
        {
            AppDomain domain = AppDomain.CurrentDomain;
            object held = domain.GetData(SlotKey);
            if (held != null || !create) return held as Dictionary<string, Dictionary<string, object>>;
            lock (domain)
            {
                held = domain.GetData(SlotKey);
                if (held == null)
                {
                    held = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
                    domain.SetData(SlotKey, held);
                }
            }
            return held as Dictionary<string, Dictionary<string, object>>;
        }

        // Lowercase letters and digits in words joined by single '-', at most 64 characters: my-feed, cars-waiting-2.
        private static bool ValidId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > MaxIdLength) return false;
            if (id[0] == '-' || id[id.Length - 1] == '-') return false;
            for (int i = 0; i < id.Length; i++)
            {
                char c = id[i];
                bool ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || (c == '-' && id[i - 1] != '-');
                if (!ok) return false;
            }
            return true;
        }
    }
}
