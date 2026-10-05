"""
cleanup_scene3.py  – v3
Complete cleanup: removes UI, Interactables, Teleport Area Setup, Hands Permissions Manager
including ALL PrefabInstance blocks, stripped objects, and their component objects.
Keeps: XR Origin Hands, Environment, Lighting, EventSystem.
"""

import re

INPUT_FILE  = r"Assets\Scenes\SampleScene.unity"
OUTPUT_FILE = r"Assets\Scenes\SampleScene.unity"
BACKUP_FILE = r"Assets\Scenes\SampleScene_backup.unity"

# Root Transform IDs to remove (and their entire subtree)
REMOVE_ROOT_TRANSFORMS = {
    "817075156",                   # Interactables
    "2046629159",                  # UI
    "1565887663878291441",         # Teleport Area Setup
}

# PrefabInstance source GUIDs to remove at root level (parent=0)
REMOVE_PREFAB_GUIDS_ROOT = {
    "9b4a657c7df58fb4fa21624fe730efa2",  # Hands Permissions Manager
}

def parse_blocks(text):
    parts = re.split(r'(?m)(?=^--- )', text)
    return [p for p in parts if p.strip()]

def anchor(block):
    m = re.match(r'--- !u!\d+ &(\S+)', block)
    return m.group(1) if m else None

def is_transform(b): return b.startswith('--- !u!4 &') or b.startswith('--- !u!224 &')
def is_go(b):        return b.startswith('--- !u!1 &')
def is_prefab(b):    return b.startswith('--- !u!1001 &')

def get_field_id(block, field):
    m = re.search(r'(?m)^  ' + re.escape(field) + r': \{fileID: (\S+?)\}', block)
    return m.group(1).rstrip(',}') if m else None

def get_name(block):
    m = re.search(r'(?m)^  m_Name: (.+)', block)
    return m.group(1).strip() if m else ""

def get_components(block):
    return re.findall(r'- component: \{fileID: (\d+)\}', block)

def get_source_guid(block):
    m = re.search(r'm_SourcePrefab:.*?guid: ([a-f0-9]+)', block)
    return m.group(1) if m else None

def get_transform_parent(block):
    m = re.search(r'm_TransformParent: \{fileID: (\d+)\}', block)
    return m.group(1) if m else None

def main():
    print(f"Reading {INPUT_FILE} ...")
    with open(INPUT_FILE, encoding="utf-8") as f:
        raw = f.read()

    with open(BACKUP_FILE, "w", encoding="utf-8") as f:
        f.write(raw)
    print(f"Backup: {BACKUP_FILE}")

    blocks = parse_blocks(raw)
    print(f"Total blocks: {len(blocks)}")

    # Build maps
    id_to_block   = {}
    transform_father = {}     # transform_id -> father_id
    transform_to_go  = {}     # transform_id -> go_id
    go_to_transform  = {}     # go_id -> transform_id
    father_to_children = {}   # father_id -> [child_transform_ids]
    prefab_blocks    = {}     # prefab_instance_id -> block

    for block in blocks:
        a = anchor(block)
        if a:
            id_to_block[a] = block

        if is_transform(block) and a:
            go_id = get_field_id(block, 'm_GameObject')
            if go_id:
                transform_to_go[a] = go_id
                go_to_transform[go_id] = a
            father = None
            m = re.search(r'(?m)^  m_Father: \{fileID: (\S+?)\}', block)
            if m:
                father = m.group(1).rstrip(',}')
            transform_father[a] = father or "0"
            if father and father != "0":
                father_to_children.setdefault(father, []).append(a)

        if is_prefab(block) and a:
            prefab_blocks[a] = block

    # Collect all transform IDs that are descendants of removed roots
    def collect_descendants(root_t_ids):
        visited = set()
        queue = list(root_t_ids)
        while queue:
            t = queue.pop()
            if t in visited: continue
            visited.add(t)
            for child in father_to_children.get(t, []):
                queue.append(child)
            # also from m_Children
            t_block = id_to_block.get(t, "")
            for c in re.findall(r'- \{fileID: (\d+)\}', t_block):
                if c not in visited:
                    queue.append(c)
        return visited

    removed_transform_ids = collect_descendants(REMOVE_ROOT_TRANSFORMS)
    removed_transform_ids.update(REMOVE_ROOT_TRANSFORMS)
    print(f"Removed transform IDs (incl. root): {len(removed_transform_ids)}")

    # Collect GO IDs and component IDs for removed transforms
    removed_go_ids = set()
    removed_comp_ids = set()
    for t_id in removed_transform_ids:
        go_id = transform_to_go.get(t_id)
        if go_id:
            removed_go_ids.add(go_id)
            go_block = id_to_block.get(go_id, "")
            comps = get_components(go_block)
            removed_comp_ids.update(comps)
        # Also collect component IDs directly in the transform block (RectTransform etc.)
        # (the transform itself is a component, already added as t_id)

    # Collect PrefabInstance IDs whose TransformParent is removed
    removed_prefab_ids = set()
    for pid, pblock in prefab_blocks.items():
        parent_id = get_transform_parent(pblock)
        if parent_id and parent_id in removed_transform_ids:
            removed_prefab_ids.add(pid)
        # Also root-level prefabs to remove by GUID
        if parent_id == "0":
            guid = get_source_guid(pblock)
            if guid in REMOVE_PREFAB_GUIDS_ROOT:
                removed_prefab_ids.add(pid)

    print(f"Removed prefab instance IDs: {len(removed_prefab_ids)}")

    # All IDs to remove
    all_remove = removed_transform_ids | removed_go_ids | removed_comp_ids | removed_prefab_ids

    # Also remove any stripped/injected objects whose m_PrefabInstance is a removed prefab
    extra = set()
    for block in blocks:
        a = anchor(block)
        m = re.search(r'm_PrefabInstance: \{fileID: (\d+)\}', block)
        if m and m.group(1) in removed_prefab_ids:
            if a:
                extra.add(a)
    all_remove.update(extra)
    print(f"Extra stripped objects: {len(extra)}")
    print(f"TOTAL IDs to remove: {len(all_remove)}")

    # Filter
    kept = []
    removed_count = 0
    for block in blocks:
        a = anchor(block)
        # Remove if anchor in remove set
        if a and a in all_remove:
            removed_count += 1
            continue
        # Remove stripped objects (no unique anchor but belong to removed prefab)
        m = re.search(r'm_PrefabInstance: \{fileID: (\d+)\}', block)
        if m and m.group(1) in removed_prefab_ids and (not a or a not in id_to_block):
            removed_count += 1
            continue
        kept.append(block)

    print(f"Removed {removed_count} blocks, kept {len(kept)} blocks.")

    # Clean m_Children references in remaining transform blocks
    result = []
    for block in kept:
        if is_transform(block):
            cleaned = re.sub(
                r'  - \{fileID: (\d+)\}\n',
                lambda m: "" if m.group(1) in all_remove else m.group(0),
                block
            )
            result.append(cleaned)
        else:
            result.append(block)

    output = "".join(result)
    with open(OUTPUT_FILE, "w", encoding="utf-8") as f:
        f.write(output)
    print(f"\nDone! Written to {OUTPUT_FILE}")

    # Summary
    print("\n=== Remaining root-level objects ===")
    for block in result:
        a = anchor(block)
        if is_transform(block) and a:
            father = transform_father.get(a, "?")
            if father == "0":
                go_id = transform_to_go.get(a, "?")
                name = get_name(id_to_block.get(go_id, "")) if go_id != "?" else "(stripped)"
                print(f"  Transform {a:<28} GO:{go_id}  [{name}]")
        if is_prefab(block) and a:
            parent = get_transform_parent(block) or "0"
            if parent == "0":
                guid = get_source_guid(block)
                print(f"  PrefabInstance {a:<22}  guid={guid}")

if __name__ == "__main__":
    main()
