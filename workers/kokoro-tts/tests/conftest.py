import os
import sys

HERE = os.path.dirname(__file__)
ROOT = os.path.abspath(os.path.join(HERE, ".."))

# Generated gRPC stubs are flat modules on the path; the worker package lives
# under ROOT. Match how the container sets PYTHONPATH.
sys.path.insert(0, os.path.join(ROOT, "generated"))
sys.path.insert(0, ROOT)
