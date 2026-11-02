import json
import pathlib
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import check_wma_std_variants as check  # noqa: E402


def report(name):
    stats = [{"name": "Samples", "value": 100}, {"name": "Coded Samples", "value": 120}]
    return json.dumps([{"root": {"name": "root", "children": [{"name": name, "children": [{"name": "Statistics", "children": stats}]}]}}])


class CheckerTests(unittest.TestCase):
    def run_checker(self, files, outputs):
        """The checker over one folder whose records are files (name -> analyzer output), with the DMO and reader at 100."""
        with tempfile.TemporaryDirectory() as tmp:
            folder = pathlib.Path(tmp)
            (folder / "records").mkdir()
            for name in files:
                (folder / name).write_bytes(b"x")
                (folder / "records" / f"{name}.json").write_text(json.dumps({"direct": {"totalSamples": 100}, "sourceReader": {"totalSamples": 100}}))
            for name in set(outputs) - set(files):
                (folder / "records" / f"{name}.json").write_text(json.dumps({"direct": {"totalSamples": 100}, "sourceReader": {"totalSamples": 100}}))

            def fake(command, **kwargs):
                name = pathlib.Path(command[-1]).name
                return subprocess.CompletedProcess(command, 0, stdout=outputs[name], stderr="")

            with mock.patch.object(subprocess, "run", fake), mock.patch.object(sys, "argv", ["x", "analyzer", str(folder)]):
                return check.main()

    def test_a_file_without_a_standard_stream_is_not_compared_and_not_unread(self):
        code = self.run_checker(["a.wma", "b.wma"], {"a.wma": report("Windows Media Audio 2"), "b.wma": report("Windows Media Audio Professional")})
        self.assertEqual(code, 0)

    def test_a_missing_file_is_unread_and_fails(self):
        code = self.run_checker(["a.wma"], {"a.wma": report("Windows Media Audio 2"), "gone.wma": ""})
        self.assertEqual(code, 1)

    def test_a_failed_parse_is_unread_and_fails(self):
        code = self.run_checker(["a.wma", "b.wma"], {"a.wma": report("Windows Media Audio 2"), "b.wma": "not json"})
        self.assertEqual(code, 1)


if __name__ == "__main__":
    unittest.main()
