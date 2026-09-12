"""Acquire pinned OTLP protos, retrying only MSBuild download failures."""

import os
from pathlib import Path
import re
import subprocess
import sys
import time


def ensure(run=subprocess.run, sleep=time.sleep, emit=print):
    project = "src/GnOuGo.OtlpCollector.Server/GnOuGo.OtlpCollector.Server.csproj"
    options = dict(cwd=Path(__file__).resolve().parents[1], text=True,
                   stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                   env={**os.environ, "DOTNET_CLI_UI_LANGUAGE": "en-US"})
    restored = run(["dotnet", "restore", project, "/p:SkipClientBuild=true"], **options)
    emit(restored.stdout)
    if restored.returncode:
        return restored.returncode

    for attempt in range(1, 4):
        result = run(["dotnet", "msbuild", project, "-t:EnsureOtlpProtos",
                      "/p:SkipClientBuild=true"], **options)
        errors = re.findall(r"\berror\s+([A-Z]+\d+)\s*:", result.stdout, re.IGNORECASE)
        download_failure = bool(errors) and all(code.upper() == "MSB3923" for code in errors)
        if result.returncode == 0 or not download_failure or attempt == 3:
            emit(result.stdout)
            return result.returncode
        # Keep recovered transport errors out of GitHub's final error annotations.
        # The final attempt's complete output is always emitted on failure.
        emit(f"OTLP protocol download failed (MSB3923), attempt {attempt}/3; retrying acquisition.")
        sleep(2)
    raise AssertionError("Unreachable")


if __name__ == "__main__":
    sys.exit(ensure())
