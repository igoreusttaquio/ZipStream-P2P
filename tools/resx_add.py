"""Adiciona/atualiza chaves nos .resx do app (en-US, pt-BR, es-ES).

Uso: python tools/resx_add.py strings.json
strings.json = { "Chave": ["english", "português", "español"], ... }
"""
import json
import re
import sys
from xml.sax.saxutils import escape

BASE = "src/ZipP2P.App/Localization/"
FILES = ["Strings.resx", "Strings.pt-BR.resx", "Strings.es-ES.resx"]


def main(path: str) -> None:
    with open(path, encoding="utf-8") as f:
        strings = json.load(f)

    for index, name in enumerate(FILES):
        file = BASE + name
        with open(file, encoding="utf-8", newline="") as f:
            content = f.read()
        nl = "\r\n" if "\r\n" in content else "\n"

        for key, values in strings.items():
            value = escape(values[index])
            block = f'  <data name="{key}" xml:space="preserve">{nl}    <value>{value}</value>{nl}  </data>{nl}'
            pattern = re.compile(
                r'  <data name="' + re.escape(key) + r'" xml:space="preserve">\s*<value>.*?</value>\s*</data>\r?\n', re.S)
            if pattern.search(content):
                content = pattern.sub(lambda _: block, content)
            else:
                content = content.replace("</root>", block + "</root>")

        with open(file, "w", encoding="utf-8", newline="") as f:
            f.write(content)


if __name__ == "__main__":
    main(sys.argv[1])
