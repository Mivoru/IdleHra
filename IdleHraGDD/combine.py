import os
import zipfile
import xml.etree.ElementTree as ET

def extract_text_from_docx(docx_path):
    try:
        with zipfile.ZipFile(docx_path) as docx:
            if 'word/document.xml' not in docx.namelist():
                return ""
            tree = ET.fromstring(docx.read('word/document.xml'))
            namespaces = {'w': 'http://schemas.openxmlformats.org/wordprocessingml/2006/main'}
            text = []
            for paragraph in tree.findall('.//w:p', namespaces):
                para_text = "".join(node.text for node in paragraph.findall('.//w:t', namespaces) if node.text)
                if para_text:
                    text.append(para_text)
            return "\n".join(text)
    except Exception as e:
        return f"Error reading {docx_path}: {e}"

folder_path = r"c:\Users\promi\skola2025\IdleHra\IdleHraGDD"
output_path = os.path.join(folder_path, "CombinedGDD.txt")

with open(output_path, "w", encoding="utf-8") as out_file:
    for filename in sorted(os.listdir(folder_path)):
        if filename.endswith(".docx") and not filename.startswith("~"):
            filepath = os.path.join(folder_path, filename)
            text = extract_text_from_docx(filepath)
            out_file.write(f"--- {filename} ---\n")
            out_file.write(text + "\n\n")

print(f"Files combined successfully into {output_path}")
