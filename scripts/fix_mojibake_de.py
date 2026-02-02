import json

import glob

def fix_mojibake(s: str) -> str:
    try:
        return s.encode("latin1").decode("utf-8")
    except UnicodeError:
        return s

files = sorted(glob.glob("Localization/strings.*.json"))

for path in files:
    with open(path, "r", encoding="utf-8-sig") as f:
        data = json.load(f)

    for k, v in data.items():
        if isinstance(v, str):
            data[k] = fix_mojibake(v)

    with open(path, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=2)

    print("Fixed:", path)
