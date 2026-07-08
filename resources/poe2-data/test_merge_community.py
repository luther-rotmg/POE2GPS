"""Tests for merge_community.py — idempotency + credit block determinism.

Run: python -m unittest resources.poe2-data.test_merge_community
(or: cd resources/poe2-data && python -m unittest test_merge_community.py)
"""
from __future__ import annotations

import io
import json
import shutil
import sys
import tempfile
import unittest
from contextlib import redirect_stdout
from pathlib import Path

# Import target
sys.path.insert(0, str(Path(__file__).parent))
import merge_community as mc  # noqa: E402


FAKE_ISSUES = [
    {
        "body": "Header text\n```json\n"
                + json.dumps({
                    "names": {"UNIQUE_A@1": "Ancient Sentinel"},
                    "objectives": [{"category": "shrines"}],
                })
                + "\n```\ntrailer",
        "author": {"login": "alice"},
        "number": 42,
        "url": "https://github.com/luther-rotmg/POE2GPS/issues/42",
    },
    {
        "body": "```json\n"
                + json.dumps({
                    "names": {"UNIQUE_B@1": "Grim Warder"},
                    "objectives": [{"category": "essences"}],
                })
                + "\n```",
        "author": {"login": "Bob"},
        "number": 7,
        "url": "https://github.com/luther-rotmg/POE2GPS/issues/7",
    },
    {
        # Same author as first, second issue — must collapse into one @handle entry.
        "body": "```json\n"
                + json.dumps({"names": {"UNIQUE_C@1": "Ashen Herald"}})
                + "\n```",
        "author": {"login": "alice"},
        "number": 101,
        "url": "https://github.com/luther-rotmg/POE2GPS/issues/101",
    },
    {
        # Deleted / bot author — must not crash, must render as @unknown.
        "body": "```json\n"
                + json.dumps({"names": {"UNIQUE_D@1": "Silent One"}})
                + "\n```",
        "author": None,
        "number": 5,
        "url": "https://github.com/luther-rotmg/POE2GPS/issues/5",
    },
]


class MergeCommunityTests(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())
        # Stage fake targets with pre-existing content.
        self.names_path = self.tmp / "entity_names.json"
        self.labels_path = self.tmp / "labels.json"
        self.names_path.write_text(
            json.dumps({"existing@1": "Old Name"}), encoding="utf-8")
        self.labels_path.write_text(
            json.dumps({"Core": ["bosses"]}, indent=2), encoding="utf-8")

        # Redirect module-level path constants.
        self._orig_names = mc.ENTITY_NAMES
        self._orig_labels = mc.LABELS
        self._orig_fetch = mc.fetch_approved_issues
        mc.ENTITY_NAMES = self.names_path
        mc.LABELS = self.labels_path
        mc.fetch_approved_issues = lambda: list(FAKE_ISSUES)

    def tearDown(self):
        mc.ENTITY_NAMES = self._orig_names
        mc.LABELS = self._orig_labels
        mc.fetch_approved_issues = self._orig_fetch
        shutil.rmtree(self.tmp, ignore_errors=True)

    def _run_capture(self) -> str:
        buf = io.StringIO()
        with redirect_stdout(buf):
            rc = mc.main([])
        self.assertEqual(rc, 0)
        return buf.getvalue()

    def test_idempotent_credit_block_and_no_double_fold(self):
        out1 = self._run_capture()
        names1 = self.names_path.read_bytes()
        labels1 = self.labels_path.read_bytes()

        out2 = self._run_capture()
        names2 = self.names_path.read_bytes()
        labels2 = self.labels_path.read_bytes()

        # Credit block emitted verbatim on both runs.
        self.assertIn("### Community contributors", out1)
        self.assertEqual(out1, out2, "credit block must be deterministic across runs")

        # No double-fold: files bit-identical after second run.
        self.assertEqual(names1, names2, "entity_names.json double-folded on rerun")
        self.assertEqual(labels1, labels2, "labels.json double-folded on rerun")

    def test_credit_block_shape(self):
        out = self._run_capture()
        # Alice's two issues collapse and sort ascending by number.
        self.assertIn("@alice", out)
        self.assertIn("#42", out)
        self.assertIn("#101", out)
        # Bob normalized case-insensitively but handle preserved.
        self.assertIn("@Bob", out)
        # Missing author rendered as @unknown, not a crash.
        self.assertIn("@unknown", out)
        # Alice appears before Bob (case-insensitive sort).
        self.assertLess(out.index("@alice"), out.index("@Bob"))

    def test_build_credit_block_pure_deterministic(self):
        contributors = [
            ("alice", [(42, "https://x/42"), (101, "https://x/101")]),
            ("Bob", [(7, "https://x/7")]),
        ]
        a = mc.build_credit_block(contributors)
        b = mc.build_credit_block(contributors)
        self.assertEqual(a, b)
        self.assertIn("@alice", a)
        self.assertIn("#42", a)


if __name__ == "__main__":
    unittest.main()
