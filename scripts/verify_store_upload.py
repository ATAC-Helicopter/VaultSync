"""Inspect the unchanged Partner Center upload without installing or signing it."""
import argparse
import json
import tempfile
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path
import shutil


def verify(upload, version, source_manifest):
    namespace = {"p": "http://schemas.microsoft.com/appx/manifest/foundation/windows10"}
    expected = ET.parse(source_manifest).getroot().find("p:Identity", namespace)
    with zipfile.ZipFile(upload) as outer:
        packages = [entry for entry in outer.infolist()
                    if Path(entry.filename).suffix.lower() in (".msix", ".appx")]
        if len(packages) != 1:
            raise ValueError("Expected exactly one unbundled Store application package")
        with tempfile.TemporaryFile() as temporary:
            with outer.open(packages[0]) as stream:
                shutil.copyfileobj(stream, temporary)
            temporary.seek(0)
            with zipfile.ZipFile(temporary) as package:
                manifest = ET.fromstring(package.read("AppxManifest.xml"))
                identity = manifest.find("p:Identity", namespace)
                for key, value in (("Name", expected.get("Name")),
                                   ("Publisher", expected.get("Publisher")),
                                   ("Version", version + ".0"),
                                   ("ProcessorArchitecture", "x64")):
                    if identity is None or identity.get(key) != value:
                        raise ValueError(f"Store identity mismatch: {key}")
                applications = manifest.findall("p:Applications/p:Application", namespace)
                if len(applications) != 1:
                    raise ValueError("Expected one Store application entry")
                executable = applications[0].get("Executable", "").replace("\\", "/")
                if not executable.endswith("VaultSync.UI.exe") or executable not in package.namelist():
                    raise ValueError("Store executable missing or unresolved")
                runtime = executable.removesuffix(".exe") + ".runtimeconfig.json"
                options = json.loads(package.read(runtime))["runtimeOptions"]
                if options.get("framework") or options.get("frameworks") or not options.get("includedFrameworks"):
                    raise ValueError("Store application is not self-contained")
                return {"identity": identity.attrib, "executable": executable,
                        "includedFrameworks": options["includedFrameworks"]}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("upload", type=Path)
    parser.add_argument("--version", required=True)
    parser.add_argument("--source-manifest", type=Path, required=True)
    arguments = parser.parse_args()
    print(json.dumps(verify(arguments.upload, arguments.version, arguments.source_manifest), indent=2))
