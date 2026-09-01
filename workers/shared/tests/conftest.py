import os
import sys

HERE = os.path.dirname(__file__)
# workers/ on the path makes `shared` importable, matching /app/shared in the images.
sys.path.insert(0, os.path.abspath(os.path.join(HERE, "..", "..")))
