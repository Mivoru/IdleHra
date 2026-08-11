import json
import time

def read_jsonl(path):
    import json
    lines = []
    with open(path, 'r', encoding='utf-8') as f:
        for line in f:
            if line.strip():
                lines.append(json.loads(line))
    return lines

print('Walkthrough preparation started')
