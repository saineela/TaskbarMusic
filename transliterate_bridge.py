"""
Python bridge for Aksharamukha transliteration.
Reads JSON from stdin:  { "source": "Telugu", "target": "Devanagari", "texts": ["...", "..."] }
Writes JSON to stdout: { "results": ["...", "..."] }
"""
import sys, json

# Force UTF-8 on stdout — Windows defaults to cp1252 which breaks Unicode output
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")

# Read stdin as raw bytes and decode with utf-8-sig to strip any BOM
# (C# ProcessStartInfo.StandardInputEncoding = UTF8 adds a BOM!)
raw = sys.stdin.buffer.read().decode("utf-8-sig")

from aksharamukha import transliterate

def main():
    if not raw.strip():
        json.dump({"results": []}, sys.stdout, ensure_ascii=False)
        return

    try:
        data = json.loads(raw)
    except json.JSONDecodeError as e:
        json.dump({"error": f"Invalid JSON: {e}"}, sys.stdout, ensure_ascii=False)
        return

    source = data.get("source", "")
    target = data.get("target", "")
    texts = data.get("texts", [])

    results = []
    for text in texts:
        try:
            result = transliterate.process(source, target, text)
            results.append(result)
        except Exception as e:
            results.append(f"[Error: {e}]")

    json.dump({"results": results}, sys.stdout, ensure_ascii=False)

if __name__ == "__main__":
    main()
