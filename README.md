# Railroader mod feeds

A small shared standard for Railroader mods to share data with each other. A mod **publishes** a feed: a name, a
description of what it holds, and a function that returns its data. Any other mod can **find** every feed loaded in the
game and **read** it, with no dependency on the mod that made it. One file, dropped into your project: nothing for
players to install. For Railroader 0.6.2 (Experimental branch), C# 7 or newer. MIT license. Revision 3 adds the reading helpers and the `changed` map.

## Use it

1. Copy `ModFeeds.cs` into your project and change only its namespace line (`namespace MODFEEDS_NAMESPACE`) to yours.
   Keep the rest exactly as it is. It needs nothing but .NET.
2. Read feeds, publish your own, or both. Call everything on the game's main thread.

### Read a feed

```csharp
foreach (ModFeeds.FeedInfo feed in ModFeeds.List())
    Log.Info(feed.Id + ": " + feed.Title + " from " + feed.Mod + " " + feed.Version);

object answer = ModFeeds.Read("cars-waiting");
if (answer is Dictionary<string, object> data && !data.ContainsKey("error"))
{
    // use data["..."]
}
```

`List()` gives every feed loaded right now, sorted by id, each with its `Title`, `Mod`, `Version`, schema version
(`Api`) and a line describing each top-level field (`Fields`). `Read(id)` gives that feed's answer. `Read` itself never
throws: a feed that isn't loaded, or one that fails, answers `{ "feed": id, "error": "..." }`. `Read(id, out double
milliseconds)` also says how long the feed took.

An answer is the publisher's data, handed over as is. Treat it as read only, ignore keys you don't know, and expect it
to be large or deeply nested now and then: if you walk it, cap how deep and how many items you go.

### Keep reading when a feed's api is newer

A feed can move on without leaving you behind: a newer `api` may still carry every key you use. Don't switch a feed
off because its api is higher than the one you wrote your reader for. Read each key you need by name and kind, and
skip only the one that isn't safe:

```csharp
object answer = ModFeeds.Read("cars-waiting");
if (ModFeeds.TryGetDouble(answer, "carsWaiting", 1, out double cars))
{
    // use cars: the feed may be on api 2 or 5, this key still means what it did at api 1
}
else
{
    // log once: missing, another kind, an error answer, or the feed says this key's meaning changed after api 1
}
```

The `1` is the api you wrote your reader for. `TryGet` (any value), `TryGetDouble` (int, long, float or double, finite),
`TryGetBool` and `TryGetString` return `false`, and never throw, when the answer is an error or isn't a dictionary,
the key is missing or of another kind, or the answer's `changed` map lists the key at an api above the one you pass.
`ApiOf(answer)` is the answer's `api` (0 if none), and `ChangedAt(answer, key)` the api its `changed` map gives for a
key (0 if not listed, `int.MaxValue` if the entry isn't a whole number, so it can't be vouched for). A `changed` map
that is present but isn't a `Dictionary<string, object>` gives `int.MaxValue` for every key, so those keys are skipped;
an absent or null one gives 0.

### Publish a feed

```csharp
internal static class MyFeed
{
    // Return plain data: dictionaries, lists, strings, numbers, bools, null.
    internal static object Read()
    {
        try
        {
            return new Dictionary<string, object>
            {
                { "api", 1 }, { "feed", "cars-waiting" },
                { "carsWaiting", 12 },
            };
        }
        catch (Exception)
        {
            return new Dictionary<string, object>
            {
                { "api", 1 }, { "feed", "cars-waiting" }, { "error", "unavailable" },
            };
        }
    }

    private static readonly Dictionary<string, string> Fields = new Dictionary<string, string>
    {
        { "carsWaiting", "cars waiting to be picked up (count)" },
    };

    // When your mod is switched on.
    internal static void Start()
    {
        if (!ModFeeds.Publish("cars-waiting", Read, "Cars waiting", "My Mod", "1.0.0", 1, Fields))
            Log.Warning("cars-waiting is already published by another mod, so this mod's feed isn't");
    }

    // When your mod is switched off.
    internal static void Stop() => ModFeeds.Withdraw("cars-waiting");
}
```

`Publish(id, read, title, mod, version, api, fields)`:
- `id`: the feed's name (see below).
- `read`: your function that returns the answer.
- `title`: a short name people see, like "Cars waiting".
- `mod`: your mod's name, and `version` its version, as players see them.
- `api`: your schema version, an int you bump when a field changes meaning (and list that field in `changed`).
- `fields`: one line per top-level key of your answer, saying what it means, with units.

It returns `false` when the id is taken by another mod or isn't a valid id, so check it. Railroader keeps a mod's code
loaded for the whole session, so publish when your mod is switched on and withdraw when it is switched off.

If your answer touches game types, keep them out of `Read` itself: call a separate method marked
`[MethodImpl(MethodImplOptions.NoInlining)]` inside `Read`'s try. Then a game update that breaks one of those types
costs your feed's answer, never your mod or the caller. `ModFeeds.cs` itself names no game type, so `Publish` and
`Withdraw` are safe to call from any lifecycle method.

### Feed ids

Lowercase letters and digits in words joined by single dashes, at most 64 characters: `cars-waiting`, `my-feed-2`.
A mod is its assembly, known by the assembly's name, so give yours a name of its own. The first mod to publish an id
keeps it; another mod publishing the same id gets `false`.
Publishing again from the same mod replaces its own entry, and Withdraw removes only your own.

## The rules for a feed

- **Plain data only:** `Dictionary<string, object>`, `List<object>`, `string`, `bool`, `int`, `long`, `float`, `double`
  or `null`, nested as deep as you need. Never your own types or the game's.
- **Start with `"api"` and `"feed"`:** your schema version and the feed's id. Use camelCase keys. No NaN or infinity.
- **Add, don't change:** a new key is fine within an `api` version. A key that changes meaning means a new `api`, and
  the key goes in a top-level `"changed"` map in the answer: `{ "rate": 2 }`, the api where its meaning last changed.
  Added keys stay out of it. Prefer a new key to changing an old one, and never rename a key.
- **Never throw.** On a failure, return `{ "api", "feed", "error" }` instead.
- **Change nothing.** Reading a feed has no side effects.
- **Cheap:** a reader may call it every couple of seconds, on the game's main thread.
- **Private by default:** no file paths, account names or machine names in an answer or in its description.
- **Describe every top-level field** in `fields`, so anyone who finds your feed knows what it means.

## Multiplayer

Each player's game answers for itself: a feed reads what that game knows. If your data only exists on the host (the
industry tick, interchange service), add `"host": true` or `false` to your answer so a reader on a client knows why
it's empty.

## How it works

Every copy of `ModFeeds.cs`, any revision, from any author, shares one list of feeds in the game process through a
frozen contract written at the top of the file: a `Dictionary<string, Dictionary<string, object>>` stored as AppDomain
data under `ashenmare.feeds.v1`, one entry per feed (its read function, title, mod, version, schema version, field
descriptions, and the publishing assembly's name as its owner). Only .NET types cross between mods, so copies from
different authors and revisions work together. Each copy changes only its own mod's entries and ignores keys it doesn't
know, so a later revision can add to an entry without breaking this one. If you change the file, keep that contract
exactly and bump `Revision`.
