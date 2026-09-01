import os
import sys

HERE = os.path.dirname(__file__)
ROOT = os.path.abspath(os.path.join(HERE, ".."))

sys.path.insert(0, os.path.join(ROOT, "generated"))
sys.path.insert(0, ROOT)

# workers/ too, so `shared` resolves the same way /app/shared does in the image.
sys.path.insert(0, os.path.abspath(os.path.join(ROOT, "..")))
