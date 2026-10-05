import re

with open('Assets/Scenes/SampleScene.unity', encoding='utf-8') as f:
    content = f.read()

checks = [
    'AudioSource', 'Audio Affordance', 'CoachingCard', 'Tutorial Player',
    'Spatial Panel', 'Blaster', 'Totem1', 'Totem2', 'Cube Interactable',
    'Cylinder Interactable', 'Sphere Interactable', 'Torus Interactable',
    'Teleport Area Setup', 'Hands Permissions'
]

print("=== Unwanted content check ===")
found_any = False
for check in checks:
    count = len(re.findall(check, content))
    if count > 0:
        print(f"  [STILL PRESENT] {check}: {count} occurrences")
        found_any = True
if not found_any:
    print("  All unwanted objects removed OK!")

blocks = re.split(r'(?m)(?=^--- )', content)
blocks = [b for b in blocks if b.strip()]
print(f"\nTotal YAML blocks: {len(blocks)}")
pi = [b for b in blocks if b.startswith('--- !u!1001')]
go = [b for b in blocks if b.startswith('--- !u!1 &')]
tr = [b for b in blocks if b.startswith('--- !u!4 &') or b.startswith('--- !u!224 &')]
print(f"  PrefabInstances : {len(pi)}")
print(f"  GameObjects     : {len(go)}")
print(f"  Transforms      : {len(tr)}")

print("\n=== Remaining named GameObjects ===")
for b in blocks:
    m = re.search(r'(?m)^  m_Name: (.+)', b)
    if m and m.group(1).strip():
        print(f"  {m.group(1).strip()}")

print("\n=== Remaining PrefabInstances (root-level, parent=0) ===")
for b in pi:
    m_parent = re.search(r'm_TransformParent: \{fileID: (\d+)\}', b)
    m_guid   = re.search(r'm_SourcePrefab:.*?guid: ([a-f0-9]+)', b)
    m_anchor = re.match(r'--- !u!\d+ &(\S+)', b)
    if m_parent and m_parent.group(1) == '0':
        pid  = m_anchor.group(1) if m_anchor else '?'
        guid = m_guid.group(1) if m_guid else '?'
        print(f"  {pid}  guid={guid}")
