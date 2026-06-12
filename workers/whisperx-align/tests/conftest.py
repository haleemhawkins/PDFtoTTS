import os
import sys

HERE = os.path.dirname(__file__)
ROOT = os.path.abspath(os.path.join(HERE, ".."))

sys.path.insert(0, os.path.join(ROOT, "generated"))
sys.path.insert(0, ROOT)
