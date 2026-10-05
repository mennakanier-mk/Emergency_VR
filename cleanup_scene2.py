"""
cleanup_scene2.py  –  v2
Uses m_Father-based reverse traversal to correctly collect ALL descendants.
Removes:
  - UI group + all descendants (Tutorial Player, Spatial Panels, CoachingCards, Audio Affordances, etc.)
  - Interactables group + all descendants
  - Teleport Area Setup group + all descendants
  - Hands Permissions Manager PrefabInstance + its stripped objects
Keeps:
  - XR Origin Hands (XR Rig) PrefabInstance
  - Environment group + all descendants
  - Lighting group + all descendants
  - EventSystem
"""

import re

INPUT_FILE  = r"Assets\Scenes\SampleScene.unity"
OUTPUT_FILE = r"Assets\Scenes\SampleScene.unity"
BACKUP_FILE = r"Assets\Scenes\SampleScene_backup.unity"

# Root group Transform fileIDs to REMOVE (we derived these above)
REMOVE_ROOT_TRANSFORM_IDS = {
    "817075156",                    # Interactables
    "2046629159",                   # UI
    "1565887663878291441",          # Teleport Area Setup (int64)
}

# PrefabInstance source GUIDs to remove
REMOVE_PREFAB_GUIDS = {
    "9b4a657c7df58fb4fa21624fe730efa2",   # Hands Permissions Manager
}

# ── Parsing ──────────────────────────────────────────────────────────────────

def parse_blocks(text):
    # Split on YAML document separators
    parts = re.split(r'(?m)(?=^--- )', text)
    # The very first part may be the file header (%YAML etc.) without ---
    result = []
    for part in parts:
        if part.strip():
            result.append(part)
    return result

def block_anchor(block):
    """Return the fileID anchor string or None."""
    m = re.match(r'--- !u!\d+ &(\S+)', block)
    return m.group(1) if m else None

def get_field(block, field):
    m = re.search(r'(?m)^  ' + re.escape(field) + r': \{fileID: (\S+?)\}', block)
    return m.group(1) if m else None

def get_source_prefab_guid(block):
    m = re.search(r'm_SourcePrefab:.*?guid: ([a-f0-9]+)', block)
    return m.group(1) if m else None

def get_name(block):
    m = re.search(r'(?m)^  m_Name: (.+)', block)
    return m.group(1).strip() if m else ""

def get_components(block):
    return re.findall(r'- component: \{fileID: (\d+)\}', block)

def is_go(block):     return block.startswith('--- !u!1 &')
def is_transform(block): return block.startswith('--- !u!4 &') or block.startswith('--- !u!224 &')
def is_prefab(block): return block.startswith('--- !u!1001 &')

# ── Build full scene graph ───────────────────────────────────────────────────

def build_graph(blocks):
    id_to_block = {}
    go_to_transform = {}        # go_id -> transform_id
    transform_to_go = {}        # transform_id -> go_id
    transform_father = {}       # transform_id -> father_transform_id (or "0")
    transform_children = {}     # transform_id -> [child_transform_id, ...]
    prefab_instances = {}       # prefab_instance_id -> block

    for block in blocks:
        anchor = block_anchor(block)
        if anchor:
            id_to_block[anchor] = block

        if is_transform(block) and anchor:
            # get father
            father = get_field(block, 'm_Father')
            transform_father[anchor] = father or "0"
            # get go
            go_id = get_field(block, 'm_GameObject')
            if go_id:
                go_to_transform[go_id] = anchor
                transform_to_go[anchor] = go_id
            # get children from m_Children list
            children = re.findall(r'- \{fileID: (\d+)\}', block)
            transform_children[anchor] = children

        if is_prefab(block) and anchor:
            prefab_instances[anchor] = block

    # Build reverse: for each transform, set its children based on m_Father
    # (Unity may list children in Transform.m_Children AND in m_Father of child)
    # We combine both approaches
    father_children = {}  # parent_transform_id -> set of child_transform_ids
    for t_id, father_id in transform_father.items():
        if father_id != "0":
            if father_id not in father_children:
                father_children[father_id] = set()
            father_children[father_id].add(t_id)

    # Merge m_Children and father_children
    all_children = {}
    all_transforms = set(transform_father.keys())
    for t_id in all_transforms:
        c1 = set(transform_children.get(t_id, []))
        c2 = father_children.get(t_id, set())
        all_children[t_id] = c1 | c2

    return id_to_block, go_to_transform, transform_to_go, transform_father, all_children, prefab_instances


def collect_subtree(root_transform_id, all_children, transform_to_go, id_to_block):
    """Collect ALL fileIDs (transforms, GOs, components) in a subtree rooted at root_transform_id."""
    to_remove = set()
    queue = list(all_children.get(root_transform_id, set())) + [root_transform_id]
    visited = set()

    while queue:
        t_id = queue.pop()
        if t_id in visited:
            continue
        visited.add(t_id)
        to_remove.add(t_id)

        # add GO + its components
        go_id = transform_to_go.get(t_id)
        if go_id:
            to_remove.add(go_id)
            go_block = id_to_block.get(go_id, "")
            comps = get_components(go_block)
            to_remove.update(comps)

        # recurse into children
        for child in all_children.get(t_id, set()):
            if child not in visited:
                queue.append(child)

    return to_remove


def main():
    print(f"Reading {INPUT_FILE} ...")
    with open(INPUT_FILE, encoding="utf-8") as f:
        raw = f.read()

    # Backup
    with open(BACKUP_FILE, "w", encoding="utf-8") as f:
        f.write(raw)
    print(f"Backup saved: {BACKUP_FILE}")

    blocks = parse_blocks(raw)
    print(f"Total YAML blocks: {len(blocks)}")

    id_to_block, go_to_transform, transform_to_go, transform_father, all_children, prefab_instances = build_graph(blocks)

    to_remove = set()

    # 1. Root group removals via Transform IDs
    for t_id in REMOVE_ROOT_TRANSFORM_IDS:
        # Also add the GO itself
        go_id = transform_to_go.get(t_id)
        if go_id:
            to_remove.add(go_id)
            go_block = id_to_block.get(go_id, "")
            to_remove.update(get_components(go_block))
        subtree = collect_subtree(t_id, all_children, transform_to_go, id_to_block)
        to_remove.update(subtree)
        name = get_name(id_to_block.get(go_id, "")) if go_id else t_id
        print(f"  Removing '{name}' subtree: {len(subtree)} objects")

    # 2. PrefabInstance removals by source GUID
    removed_prefab_ids = set()
    for pid, block in prefab_instances.items():
        guid = get_source_prefab_guid(block)
        if guid in REMOVE_PREFAB_GUIDS:
            to_remove.add(pid)
            removed_prefab_ids.add(pid)
            print(f"  Removing PrefabInstance {pid} (guid={guid})")

    # 3. Stripped/injected objects belonging to removed prefab instances
    for block in blocks:
        anchor = block_anchor(block)
        m = re.search(r'm_PrefabInstance: \{fileID: (\d+)\}', block)
        if m and m.group(1) in removed_prefab_ids:
            if anchor:
                to_remove.add(anchor)

    print(f"\nTotal IDs to remove: {len(to_remove)}")

    # Filter blocks
    kept = []
    removed_count = 0
    for block in blocks:
        anchor = block_anchor(block)
        if anchor and anchor in to_remove:
            removed_count += 1
            continue
        # Remove stripped objects of removed prefab instances (no anchor)
        m = re.search(r'm_PrefabInstance: \{fileID: (\d+)\}', block)
        if m and m.group(1) in removed_prefab_ids:
            removed_count += 1
            continue
        kept.append(block)

    print(f"Removed {removed_count} blocks. Keeping {len(kept)} blocks.")

    # Clean up m_Children lists in Transform blocks to remove dangling refs
    result = []
    for block in kept:
        if is_transform(block):
            # Remove child lines pointing to removed IDs
            def fix_child(match):
                child_id = match.group(1)
                if child_id in to_remove:
                    return ""
                return match.group(0)
            cleaned = re.sub(r'  - \{fileID: (\d+)\}\n', fix_child, block)
            result.append(cleaned)
        else:
            result.append(block)

    output = "".join(result)

    with open(OUTPUT_FILE, "w", encoding="utf-8") as f:
        f.write(output)

    print(f"\nWritten to: {OUTPUT_FILE}")

    # Summary: list root-level objects remaining
    print("\n=== Remaining root-level objects ===")
    for block in result:
        if is_transform(block):
            anchor = block_anchor(block)
            father = transform_father.get(anchor, "?")
            if father == "0":
                go_id = transform_to_go.get(anchor, "?")
                name = get_name(id_to_block.get(go_id, "")) if go_id != "?" else "(prefab-stripped)"
                print(f"  Transform {anchor} -> GO {go_id}  [{name}]")
        if is_prefab(block):
            anchor = block_anchor(block)
            # check TransformParent == 0
            m = re.search(r'm_TransformParent: \{fileID: (\d+)\}', block)
            if m and m.group(1) == "0":
                guid = get_source_prefab_guid(block)
                print(f"  PrefabInstance {anchor}  (guid={guid})")


if __name__ == "__main__":
    main()
