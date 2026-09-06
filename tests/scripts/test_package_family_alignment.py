import unittest
import xml.etree.ElementTree as ET
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]


class PackageFamilyAlignmentTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        document = ET.parse(ROOT / "Directory.Packages.props")
        cls.versions = {
            item.attrib["Include"]: item.attrib["Version"]
            for item in document.findall(".//PackageVersion")
        }

    def assert_family_is_aligned(self, prefix: str, minimum_members: int) -> None:
        family = {
            name: version
            for name, version in self.versions.items()
            if name == prefix or name.startswith(f"{prefix}.")
        }
        self.assertGreaterEqual(len(family), minimum_members)
        self.assertEqual(
            1,
            len(set(family.values())),
            f"{prefix} packages must use one version across managed and native assets: {family}",
        )

    def test_avalonia_packages_use_one_version(self) -> None:
        self.assert_family_is_aligned("Avalonia", 6)

    def test_skiasharp_packages_use_one_version(self) -> None:
        self.assert_family_is_aligned("SkiaSharp", 6)

    def test_harfbuzzsharp_packages_use_one_version(self) -> None:
        self.assert_family_is_aligned("HarfBuzzSharp", 5)


if __name__ == "__main__":
    unittest.main()
