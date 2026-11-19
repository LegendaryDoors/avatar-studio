// The seam between the avatar tool and the games that use its avatars (PlayerAvatar.cs).

using System;
using System.Collections.Generic;

namespace AvatarStudio
{
    interface IGameTarget
    {
        /// <summary>The game's name in the UI.</summary>
        string Name { get; }

        /// <summary>For a button legend, where the full name won't fit.</summary>
        string ShortName { get; }

        /// <summary>Installed beside us; Discover() returns only present ones.</summary>
        bool IsPresent { get; }

        /// <summary>Some targets can only write a profile.</summary>
        bool CanLaunch { get; }

        /// <summary>The name of the avatar the game uses, or null.</summary>
        string CurrentName { get; }

        /// <summary>The game's 1000-byte manifest, or null.</summary>
        byte[] CurrentManifest { get; }

        /// <summary>Point the game at this avatar; throws on failure.</summary>
        void Apply(string name, byte[] manifest);

        /// <summary>False, with a logged reason, when it could not start.</summary>
        bool Launch();

        /// <summary>One line for the UI and the log: what the game is set to.</summary>
        string Status { get; }
    }

    /// <summary>A target that can be looked at but not used: Apply and Launch throw.
    /// Scripted runs wrap every target in one, so they can never write a real profile.</summary>
    sealed class ReadOnlyGameTarget : IGameTarget
    {
        private readonly IGameTarget inner;
        public ReadOnlyGameTarget(IGameTarget inner) { this.inner = inner; }

        public string Name { get { return inner.Name; } }
        public string ShortName { get { return inner.ShortName; } }
        public bool IsPresent { get { return inner.IsPresent; } }
        public bool CanLaunch { get { return false; } }
        public string CurrentName { get { return inner.CurrentName; } }
        public byte[] CurrentManifest { get { return inner.CurrentManifest; } }
        public string Status { get { return inner.Status + " (read-only)"; } }

        public void Apply(string name, byte[] manifest)
        {
            throw new InvalidOperationException(Name + " is read-only in this run");
        }

        public bool Launch()
        {
            Console.WriteLine("[studio] " + Name + " is read-only in this run; not launching");
            return false;
        }
    }

    static class GameTargets
    {
        /// <summary>Every game present beside us; `readOnly` wraps them for scripted runs.</summary>
        public static List<IGameTarget> Discover(bool readOnly)
        {
            List<IGameTarget> found = new List<IGameTarget>();
            foreach (IGameTarget t in All())
            {
                bool present;
                try { present = t.IsPresent; }
                catch (Exception e) { Console.WriteLine("[studio] " + t.Name + ": " + e.Message); continue; }
                if (present) found.Add(readOnly ? (IGameTarget)new ReadOnlyGameTarget(t) : t);
            }
            Console.WriteLine("[studio] game targets: " + (found.Count == 0 ? "none (avatar library only)" : Describe(found)));
            return found;
        }

        // the player avatar: games read it from the studio's folder, so the studio names none
        private static IEnumerable<IGameTarget> All()
        {
            yield return new PlayerAvatar();
        }

        private static string Describe(List<IGameTarget> found)
        {
            string s = "";
            foreach (IGameTarget t in found) s += (s.Length > 0 ? ", " : "") + t.Name + " (" + t.Status + ")";
            return s;
        }
    }
}
