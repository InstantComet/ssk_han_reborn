import json
with open('para/menu.json', encoding='utf-8') as f:
    data = json.load(f)
print(f'Loaded {len(data)} entries')
for e in data[:5]:
    orig = e.get("original")
    trans = e.get("translation")
    print(f'  [{orig}] -> [{trans}]')
