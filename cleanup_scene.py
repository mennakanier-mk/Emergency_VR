"""
cleanup_scene.py
Removes UI panels, audio affordances, interactables, teleport setup, and hands permissions
from SampleScene.unity, keeping only:
  - XR Origin Hands (XR Rig) prefab
  - Environment group (Template Environment, Grid, etc.)
  - Lighting group
  - EventSystem
  - XR Interaction Manager (if present)
"""

import re
import sys

INPUT_FILE  = r"Assets\Scenes\SampleScene.unity"
OUTPUT_FILE = r"Assets\Scenes\SampleScene.unity"
BACKUP_FILE = r"Assets\Scenes\SampleScene_backup.unity"

# ── Root GameObjects to REMOVE (fileID as strings) ──────────────────────────
# Interactables  : 817075155  / Transform: 817075156
# UI             : 2046629158 / Transform: 2046629159
# Teleport Area Setup (int64): 1565887663878291440
REMOVE_ROOT_GO_IDS = {
    "817075155",
    "2046629158",
    "1565887663878291440",
}

# Root PrefabInstances to REMOVE by source GUID
# Hands Permissions Manager: source guid 9b4a657c7df58fb4fa21624fe730efa2
REMOVE_PREFAB_GUIDS = {
    "9b4a657c7df58fb4fa21624fe730efa2",
}

# ── Helper ───────────────────────────────────────────────────────────────────

def parse_blocks(text):
    """Split YAML into (header, body) tuples where header is e.g. '--- !u!1 &12345'."""
    # Split on lines starting with '---'
    parts = re.split(r'(?m)^(?=---)', text)
    blocks = []
    for part in parts:
        if part.strip():
            blocks.append(part)
    return blocks

def get_file_id(block):
    """Extract the fileID anchor from a YAML block header like '--- !u!1 &12345'."""
    m = re.match(r'---\s+!u!\d+\s+&(\S+)', block)
    if m:
        return m.group(1)
    # stripped objects (no anchor)
    return None

def get_name(block):
    m = re.search(r'  m_Name: (.+)', block)
    if m:
        return m.group(1).strip()
    return ""

def get_prefab_source_guid(block):
    """For PrefabInstance blocks, get the m_SourcePrefab guid."""
    m = re.search(r'm_SourcePrefab:.*?guid: ([a-f0-9]+)', block)
    if m:
        return m.group(1)
    return None

def get_transform_children(block):
    """Return list of child fileID strings from a Transform block."""
    children = re.findall(r'- \{fileID: (\d+)\}', block)
    return children

def get_father(block):
    m = re.search(r'  m_Father: \{fileID: (\d+)\}', block)
    if m:
        return m.group(1)
    return None

def is_transform_block(block):
    return bool(re.match(r'---\s+!u!4\s+', block)) or bool(re.match(r'---\s+!u!224\s+', block))

def is_gameobject_block(block):
    return bool(re.match(r'---\s+!u!1\s+', block))

def is_prefabinstance_block(block):
    return bool(re.match(r'---\s+!u!1001\s+', block))

def get_go_from_transform(block, blocks_by_id):
    """Given a Transform block, find its parent GO fileID."""
    m = re.search(r'  m_GameObject: \{fileID: (\d+)\}', block)
    if m:
        return m.group(1)
    return None

# ── Build maps ────────────────────────────────────────────────────────────────

def build_maps(blocks):
    """Return dicts needed for traversal."""
    id_to_block  = {}   # fileID -> block text
    id_to_idx    = {}   # fileID -> index in blocks list
    transform_children = {}  # transform_fileID -> [child_transform_fileIDs]
    go_to_transform = {}     # go_fileID -> transform_fileID
    transform_to_go = {}     # transform_fileID -> go_fileID
    prefab_id_to_block = {}  # prefab_instance_id -> block

    for idx, block in enumerate(blocks):
        fid = get_file_id(block)
        if fid:
            id_to_block[fid] = block
            id_to_idx[fid]   = idx

        if is_transform_block(block) and fid:
            children = get_transform_children(block)
            transform_children[fid] = children
            # find m_GameObject
            m = re.search(r'  m_GameObject: \{fileID: (\d+)\}', block)
            if m:
                go_id = m.group(1)
                go_to_transform[go_id] = fid
                transform_to_go[fid]   = go_id

        if is_gameobject_block(block) and fid:
            pass  # handled above

        if is_prefabinstance_block(block) and fid:
            prefab_id_to_block[fid] = block

    return id_to_block, id_to_idx, transform_children, go_to_transform, transform_to_go, prefab_id_to_block


def collect_subtree_fileids(root_transform_id, transform_children, transform_to_go, id_to_block):
    """BFS/DFS collect all fileIDs (transforms + GOs + components) in a subtree."""
    to_remove = set()
    queue = [root_transform_id]
    visited = set()

    while queue:
        t_id = queue.pop()
        if t_id in visited:
            continue
        visited.add(t_id)
        to_remove.add(t_id)

        # add GO
        go_id = transform_to_go.get(t_id)
        if go_id:
            to_remove.add(go_id)
            # add all components listed in GO block
            go_block = id_to_block.get(go_id, "")
            comp_ids = re.findall(r'- component: \{fileID: (\d+)\}', go_block)
            to_remove.update(comp_ids)

        # recurse children
        for child_t in transform_children.get(t_id, []):
            queue.append(child_t)

    return to_remove


def collect_prefab_fileids_by_guid(prefab_id_to_block, remove_guids):
    """Find PrefabInstance blocks whose source guid matches remove_guids, return their fileIDs."""
    pids = set()
    for pid, block in prefab_id_to_block.items():
        guid = get_prefab_source_guid(block)
        if guid in remove_guids:
            pids.add(pid)
            # also collect any stripped/added objects referencing this prefab instance
    return pids


# ── Main ─────────────────────────────────────────────────────────────────────

def main():
    print(f"Reading {INPUT_FILE} ...")
    with open(INPUT_FILE, encoding="utf-8") as f:
        raw = f.read()

    # Save backup
    with open(BACKUP_FILE, "w", encoding="utf-8") as f:
        f.write(raw)
    print(f"Backup saved to {BACKUP_FILE}")

    blocks = parse_blocks(raw)
    print(f"Total blocks: {len(blocks)}")

    (id_to_block, id_to_idx, transform_children,
     go_to_transform, transform_to_go, prefab_id_to_block) = build_maps(blocks)

    to_remove = set()

    # 1. Remove root GOs (Interactables, UI, Teleport Area Setup) and their whole subtrees
    for go_id in REMOVE_ROOT_GO_IDS:
        to_remove.add(go_id)
        t_id = go_to_transform.get(go_id)
        if t_id:
            subtree = collect_subtree_fileids(t_id, transform_children, transform_to_go, id_to_block)
            to_remove.update(subtree)
            print(f"  Removing root GO '{get_name(id_to_block.get(go_id,''))}' (id={go_id}) + {len(subtree)} objects in subtree")
        else:
            print(f"  Removing root GO id={go_id} (no transform found, removing GO only)")

    # 2. Remove Hands Permissions Manager prefab instances by source GUID
    prefab_ids = collect_prefab_fileids_by_guid(prefab_id_to_block, REMOVE_PREFAB_GUIDS)
    for pid in prefab_ids:
        to_remove.add(pid)
        print(f"  Removing PrefabInstance id={pid} (Hands Permissions Manager)")

    # 3. Also remove any stripped objects whose m_PrefabInstance references a removed prefab
    #    and any component block whose fileID is in to_remove
    # Find all blocks referencing removed prefab instances (stripped objects)
    for block in blocks:
        m = re.search(r'm_PrefabInstance: \{fileID: (\d+)\}', block)
        if m and m.group(1) in prefab_ids:
            fid = get_file_id(block)
            if fid:
                to_remove.add(fid)

    print(f"\nTotal blocks to remove: {len(to_remove)}")

    # Filter blocks
    kept_blocks = []
    removed_count = 0
    for block in blocks:
        fid = get_file_id(block)
        if fid and fid in to_remove:
            removed_count += 1
            continue
        # Also filter stripped objects referencing removed prefab instances
        m = re.search(r'm_PrefabInstance: \{fileID: (\d+)\}', block)
        if m and m.group(1) in (to_remove & set(prefab_id_to_block.keys())):
            removed_count += 1
            continue
        kept_blocks.append(block)

    print(f"Removed {removed_count} blocks. Keeping {len(kept_blocks)} blocks.")

    # Now clean up dangling child references in Transform blocks
    # For each Transform we keep, remove child references that point to removed transforms
    result_blocks = []
    for block in kept_blocks:
        if is_transform_block(block):
            fid = get_file_id(block)
            # Remove child lines pointing to removed IDs
            def replace_children(m_inner):
                child_id = m_inner.group(1)
                if child_id in to_remove:
                    return ""  # remove this line
                return m_inner.group(0)
            cleaned = re.sub(r'  - \{fileID: (\d+)\}\n', replace_children, block)
            result_blocks.append(cleaned)
        else:
            result_blocks.append(block)

    output = "".join(result_blocks)

    with open(OUTPUT_FILE, "w", encoding="utf-8") as f:
        f.write(output)

    print(f"\nDone! Written to {OUTPUT_FILE}")
    print("Summary of kept root objects:")
    # Print remaining root-level transforms (m_Father: {fileID: 0})
    for block in result_blocks:
        if is_transform_block(block):
            father = get_father(block)
            if father == "0":
                fid = get_file_id(block)
                go_id = transform_to_go.get(fid, "?")
                name = get_name(id_to_block.get(go_id, "")) if go_id != "?" else "prefab-root"
                print(f"  - [{fid}] GO:{go_id}  Name: '{name}'")

if __name__ == "__main__":
    main()
