"""One library, two engines. The C# tests (CrossEngineTests) run this around their own work:

    python crosscheck.py make DIR     the Python engine builds a library in DIR and describes it in DIR/python.json
    (the C# engine then reads it, moves, deletes, adds, and files notes, and lists what it did in DIR/csharp.json)
    python crosscheck.py verify DIR   the Python engine checks every file and row the C# engine wrote

so a computer can switch engines with its library as it is.
"""

import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

from granola_share.config import ClassDef, Config, load_config, save_config  # noqa: E402
from granola_share.granola import Meeting, meeting_from_dict, meeting_to_dict  # noqa: E402
from granola_share.pipeline import Pipeline  # noqa: E402
from granola_share.store import Classification, Store, render_markdown  # noqa: E402

TRANSCRIPT = "Membranes keep the cell's inside in and the outside out. " * 40


def make(d: Path) -> None:
    cfg = Config(home=d / "home", pool_dir=d / "pool", ollama_enabled=False, pool_password="maple-otter",
                 classes=[ClassDef("Bio 110", ["biology"]), ClassDef("Chem 101"), ClassDef("CS 101", ["cs101"])])
    save_config(cfg)
    st = Store(cfg.db_path, cfg.pool_dir)
    raw = {"id": "alpha-111111", "score": 1.5, "tags": ["é", "😀"], "nested": {"n": None, "ok": True}}
    alpha = Meeting(id="alpha-111111", title="Cells — part 1", date="2026-09-01T09:00:00", owner="Sam",
                    attendees=["Sam", "Bo"], folder="Biology", notes_markdown="Granola's notes", transcript=TRANSCRIPT,
                    raw=raw)
    st.save(alpha, Classification("Bio 110", 0.9, "ollama", "Cell membranes", ["cells", "membranes"]),
            summary_md="## Overview\nMembranes.\n### Detail", summary_model="big:35b")
    beta = Meeting(id="beta-222222", title="Cells — part 1", date="2026-09-01", owner="Sam", notes_markdown="b")
    st.save(beta, Classification("Bio 110", 0.5, "rules"))  # same day and title: gets "[222222]" in its name
    st.save(Meeting(id="gamma-333333", title="Review 🧬: \"everything\"?", date="2026-09-03", notes_markdown="g",
                    private_notes="ask about HW3"), Classification("Unsorted", 0.0, "none"))
    st.save(Meeting(id="delta-444444", title="Delete me", date="2026-09-04"), Classification("Chem 101", 0.85, "rules"))
    st.enqueue(Meeting(id="queued-1", title="Queued lecture", date="2026-09-05", transcript="short"))
    st.enqueue(Meeting(id="failed-1", title="Stuck", date="2026-09-06"))
    st.conn.execute("UPDATE notes SET status='failed', error='boom' WHERE id='failed-1'")
    st.conn.commit()
    st.set_state("last_sync", "2026-09-15T00:00:00+00:00")
    ids = [r["id"] for r in st.conn.execute("SELECT id FROM notes ORDER BY id")]
    out = {
        "home": str(cfg.home),
        "rows": {i: dict(st.get(i)) for i in ids},
        "meetings": {i: meeting_to_dict(st.meeting(st.get(i))) for i in ids},
        "list": [r["id"] for r in st.list_notes()],
        "search": [r["id"] for r in st.search("membranes")],
        "classes": st.classes_summary(),
        "counts": st.status_counts(),
        "processing": [r["id"] for r in st.processing()],
    }
    (d / "python.json").write_text(json.dumps(out, indent=1), encoding="utf-8")


def verify(d: Path) -> None:
    did = json.loads((d / "csharp.json").read_text(encoding="utf-8"))
    cfg = load_config(d / "home")
    assert cfg.pool_password == "maple-otter" and cfg.class_names() == ["Bio 110", "Chem 101", "CS 101"]
    st = Store(cfg.db_path, cfg.pool_dir)
    cols = [r["name"] for r in st.conn.execute("PRAGMA table_info(notes)")]
    assert cols == did["columns"], (cols, did["columns"])
    for row in st.conn.execute("SELECT * FROM notes"):
        if row["payload_json"]:  # both engines write the lecture the same way
            again = json.dumps(meeting_to_dict(meeting_from_dict(json.loads(row["payload_json"]))))
            assert again == row["payload_json"], row["id"]
        if not row["md_path"]:
            continue
        path = Path(row["md_path"])
        assert path.exists(), f"{row['id']}: {path} is missing"
        c = Classification(row["class_name"], row["confidence"], row["classified_by"], row["lecture_title"] or "",
                           json.loads(row["topics"] or "[]"))
        want = render_markdown(st.meeting(row), c, row["summary_md"] or "", row["summary_model"] or "",
                               keep_granola=row["id"] in did["keep_granola"])
        assert path.read_text(encoding="utf-8") == want, f"{row['id']}: {path.name} differs from Python's"
        assert path.read_bytes().count(b"\r\n") == (want.count("\n") if sys.platform == "win32" else 0), path.name
    alpha = st.get("alpha-111111")
    assert (alpha["class_name"], alpha["classified_by"]) == ("Chem 101", "human")
    assert Path(alpha["md_path"]).parent.name == "Chem 101" and alpha["summary_md"].startswith("## Overview")
    assert st.get("delta-444444") is None and not any(cfg.pool_dir.rglob("Delete me*"))
    assert (cfg.pool_dir / "Bio 110").exists()  # beta is still there
    for i in did["filed"]:
        assert st.get(i)["status"] == "done", i
    assert st.get_state("csharp") == "yes" and st.get_state("last_sync") == "2026-09-15T00:00:00+00:00"
    # The Python engine can carry on where the C# engine stopped.
    assert st.requeue_all() >= 1
    assert Pipeline(cfg, st, log=lambda *_: None).run_pending() >= 1
    assert all(r["status"] == "done" for r in st.conn.execute("SELECT status FROM notes"))
    print("ok")


if __name__ == "__main__":
    step, where = sys.argv[1], Path(sys.argv[2])
    {"make": make, "verify": verify}[step](where)
