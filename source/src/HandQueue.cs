using System;
using System.Collections.Generic;
using UnityEngine;

namespace ScopeHandjob
{
    /// <summary>
    /// Left-hand queue shared by the Handjob mods (Scope Handjob and other mods that use the left hand). The same file is in each mod.
    /// It lives in AppDomain data, so the DLLs need no reference to each other. Unity main thread only: no locks.
    /// Ticket = object[] { string owner, float lastBeat, string what, float joinedAt }, times in Time.unscaledTime.
    /// Rules (user 2026-11-26): first come first served, the others wait; a press that cannot start within 2 s is
    /// dropped, never done without the hand. A mod only removes its own tickets.
    /// </summary>
    internal static class HandQueue
    {
        internal const string Key = "Handjob.LeftHand.v1";
        internal const float StaleAfter = 1f; // a ticket with no heartbeat for this long is dropped (mod gone / raid over)
        internal const float MaxWait = 2f;    // a press that could not start within this is dropped (user 2026-11-26)

        private static List<object[]> Table()
        {
            AppDomain domain = AppDomain.CurrentDomain;
            if (!(domain.GetData(Key) is List<object[]> table))
            {
                table = new List<object[]>();
                domain.SetData(Key, table);
            }
            return table;
        }

        private static void Purge()
        {
            float now = Time.unscaledTime;
            Table().RemoveAll(t => t == null || t.Length < 4 || !(t[1] is float beat) || now - beat > StaleAfter);
        }

        internal static object[] Join(string owner, string what)
        {
            Purge();
            float now = Time.unscaledTime;
            var ticket = new object[] { owner, now, what, now };
            Table().Add(ticket);
            return ticket;
        }

        internal static void Beat(object[] ticket)
        {
            if (ticket != null) ticket[1] = Time.unscaledTime;
        }

        internal static void Leave(object[] ticket)
        {
            if (ticket != null) Table().Remove(ticket);
        }

        internal static bool Holds(object[] ticket) => ticket != null && Table().Contains(ticket);

        /// <summary>Nobody holds or waits for the hand.</summary>
        internal static bool Empty()
        {
            Purge();
            return Table().Count == 0;
        }

        internal static bool IsFront(object[] ticket)
        {
            Purge();
            List<object[]> table = Table();
            return ticket != null && table.Count > 0 && ReferenceEquals(table[0], ticket);
        }

        internal static bool IsLast(object[] ticket)
        {
            List<object[]> table = Table();
            return ticket != null && table.Count > 0 && ReferenceEquals(table[table.Count - 1], ticket);
        }

        /// <summary>Someone other than `owner` is waiting in the table (for the quick hand-over).</summary>
        internal static bool OthersWaiting(string owner)
        {
            Purge();
            foreach (object[] t in Table())
                if (!(t[0] is string o && o == owner)) return true;
            return false;
        }

        internal static float Waited(object[] ticket) => ticket != null && ticket[3] is float at ? Time.unscaledTime - at : 0f;

        /// <summary>For logs: who holds the front ticket and why.</summary>
        internal static string Front()
        {
            Purge();
            List<object[]> table = Table();
            return table.Count == 0 ? "nobody" : $"{table[0][0]} ({table[0][2]})";
        }
    }
}
