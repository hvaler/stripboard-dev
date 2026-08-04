import os
import sys
import subprocess

def run_full_demo():
    """
    Orchestrates the single-command reproducible demo environment (§8 of brief).
    """
    sys.stdout.reconfigure(encoding='utf-8')
    print("Starting Stripboard Automated Demo Harness...")
    
    # 1. Run Disruption Pipeline
    demo_script = os.path.join(os.path.dirname(__file__), "inject_disruption.py")
    subprocess.check_call([sys.executable, demo_script])

    print("\nDemo harness completed cleanly.")

if __name__ == "__main__":
    run_full_demo()
