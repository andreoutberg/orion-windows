using System;
using System.Collections.Generic;
using System.Linq;

namespace Orion
{
    public sealed record TabSearchMatch(BrowserTab? Tab, TabGroup Group)
    {
        public string Title => Tab == null ? WorkspaceName(Group)
            : string.IsNullOrWhiteSpace(Tab.Title) ? Tab.Url : Tab.Title;

        public string Context
        {
            get
            {
                if (Tab == null)
                    return "Workspace";

                string workspace = WorkspaceName(Group);
                if (!Uri.TryCreate(Tab.Url, UriKind.Absolute, out Uri? uri))
                    return workspace;

                string site = uri.IsFile ? "Local file" : uri.Host.Length == 0 ? uri.Scheme : uri.Host;
                return $"{workspace}  ·  {site}";
            }
        }

        public override string ToString() => $"{Title}, {Context}";

        public static IEnumerable<TabSearchMatch> Find(IEnumerable<TabGroup> groups, TabGroup? active, string query)
        {
            query = query.Trim();
            TabGroup[] ordered = [.. groups.OrderByDescending(group => group == active)];
            foreach (TabGroup group in ordered)
            {
                if (Contains(WorkspaceName(group), query))
                    yield return new TabSearchMatch(null, group);
            }

            HashSet<long> seenPins = [];
            foreach (TabGroup group in ordered)
            {
                bool matchesGroup = Contains(WorkspaceName(group), query);
                foreach (BrowserTab tab in group.FlattenedTabs)
                {
                    var match = new TabSearchMatch(tab, group);
                    if (!matchesGroup && !Contains(match.Title, query) && !Contains(tab.Url, query)
                        && (tab.Pin is not { } pin || (!Contains(pin.Title, query) && !Contains(pin.Url, query))))
                        continue;

                    // A profile-wide pin appears in multiple groups with the same database ID.
                    if (tab.Pin?.Scope == TabPinScope.Profile && tab.Id != 0 && !seenPins.Add(tab.Id))
                        continue;

                    yield return match;
                }
            }
        }

        private static string WorkspaceName(TabGroup group) => group.IsSaved ? group.Name : "This Window";

        private static bool Contains(string text, string query) => text.Contains(query, StringComparison.OrdinalIgnoreCase);
    }
}
