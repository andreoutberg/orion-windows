using System;
using System.Collections.Generic;
using System.Linq;

namespace Orion
{
    internal sealed record WorkspacePinFolder(Guid Id, string Name, bool IsExpanded, long[] TabIds);

    internal sealed record WorkspacePinFolderState(int Version, WorkspacePinFolder[] Folders)
    {
        public const int CurrentVersion = 1;

        public WorkspacePinFolderState Repair(IEnumerable<long> workspacePinIds)
        {
            var unassigned = new HashSet<long>(workspacePinIds);
            var folderIds = new HashSet<Guid>();
            return this with
            {
                Folders = Folders
                    .Where(folder => folder != null && folder.Id != Guid.Empty
                        && !string.IsNullOrWhiteSpace(folder.Name) && folderIds.Add(folder.Id))
                    .Select(folder => folder with
                    {
                        Name = folder.Name.Trim(),
                        TabIds = (folder.TabIds ?? []).Where(unassigned.Remove).ToArray(),
                    }).ToArray(),
            };
        }
    }
}
