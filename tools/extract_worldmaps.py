# -*- coding: utf-8 -*-
"""
Extrait les fonds de carte des continents depuis le client WoW 3.3.5a et les
assemble en PNG, pour le module Carte des joueurs d'AzerothManager.

Les images restent sur le poste : ce sont des ressources du jeu, elles n'ont
rien à faire dans le dépôt. Ce script les régénère quand on en a besoin.

Contrairement à ce qu'on suppose, l'art d'interface et les DBC ne sont pas dans
common.MPQ mais dans l'archive de LANGUE, sous Data\\<locale>\\locale-<locale>.MPQ.

Chaque carte est un damier de douze tuiles BLP de 256x256, quatre en largeur et
trois en hauteur. Le client n'en affiche que 1002x668 : on recadre pareil, sans
quoi la projection serait décalée.

Dépendances : pip install mpyq pillow

Usage :
    python extract_worldmaps.py "E:\\WoW Clients\\3.3.5a"
"""
import os
import sys
import glob
import io

from mpyq import MPQArchive
from PIL import Image

CONTINENTS = ["Azeroth", "Kalimdor", "Expansion01", "Northrend"]

TILE = 256
COLUMNS, ROWS = 4, 3
# Zone réellement affichée par le client dans le damier 1024x768.
VISIBLE_WIDTH, VISIBLE_HEIGHT = 1002, 668


def client_locale(data_path):
    """
    Langue réellement installée : c'est le seul dossier portant un locale-<langue>.mpq.
    Les autres dossiers ne contiennent que des patchs, et les prendre reviendrait à
    tirer l'art d'une langue qui n'est pas celle du client.
    """
    for entry in os.listdir(data_path):
        folder = os.path.join(data_path, entry)
        if os.path.isdir(folder) and os.path.exists(
                os.path.join(folder, f"locale-{entry.lower()}.mpq")):
            return entry.lower()
    return None


def archives(client_path):
    """Archives par priorité décroissante : un patch remplace l'archive de base."""
    data = os.path.join(client_path, "Data")
    locale = client_locale(data)

    found = []
    for root, _, files in os.walk(data):
        for name in files:
            if name.lower().endswith(".mpq"):
                found.append(os.path.join(root, name))

    def rank(path):
        relative = os.path.relpath(path, data).lower()
        parts = relative.split(os.sep)
        # Écarte les dossiers de langue étrangère : leur art est le même, mais
        # le tirer de là serait fortuit.
        foreign = len(parts) > 1 and locale is not None and parts[0] != locale
        # Un patch prime sur l'archive de base.
        patch = os.path.basename(relative).startswith("patch")
        return (1 if foreign else 0, 0 if patch else 1, relative)

    found.sort(key=rank)
    return found


def read_tile(opened, name):
    """Première archive, par ordre de priorité, qui contient la tuile."""
    for path, archive in opened:
        try:
            data = archive.read_file(name)
        except Exception:
            continue
        if data:
            return data, os.path.basename(path)
    return None, None


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 1

    client = sys.argv[1]
    output = os.path.join(os.environ["APPDATA"], "AzerothManager", "maps")
    os.makedirs(output, exist_ok=True)

    opened = []
    for path in archives(client):
        try:
            opened.append((path, MPQArchive(path, listfile=False)))
        except Exception:
            pass

    if not opened:
        print("Aucune archive MPQ lisible sous", client)
        return 1

    print(f"{len(opened)} archives ouvertes\n")

    for continent in CONTINENTS:
        canvas = Image.new("RGB", (TILE * COLUMNS, TILE * ROWS))
        missing = []
        source = None

        for index in range(1, COLUMNS * ROWS + 1):
            entry = f"Interface\\WorldMap\\{continent}\\{continent}{index}.blp"
            data, origin = read_tile(opened, entry)
            if data is None:
                missing.append(index)
                continue
            source = source or origin
            tile = Image.open(io.BytesIO(data)).convert("RGB")
            column, row = (index - 1) % COLUMNS, (index - 1) // COLUMNS
            canvas.paste(tile, (column * TILE, row * TILE))

        if missing:
            print(f"  {continent:12} tuiles manquantes : {missing}")
            if len(missing) == COLUMNS * ROWS:
                continue

        canvas = canvas.crop((0, 0, VISIBLE_WIDTH, VISIBLE_HEIGHT))
        target = os.path.join(output, continent + ".png")
        canvas.save(target, "PNG")
        size = os.path.getsize(target) // 1024
        print(f"  {continent:12} -> {target}  ({size} Ko, depuis {source})")

    for _, archive in opened:
        try:
            archive.file.close()
        except Exception:
            pass

    print(f"\nTerminé. Les cartes sont dans {output}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
