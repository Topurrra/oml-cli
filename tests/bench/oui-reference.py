"""Builds the OUI kit spec for a page template ALONE, for the OUI benchmark reference (tests/bench/compare.ps1).

ouikit.py --screens T always adds the templates that T's menu links to. The benchmark subset is one template and only
the blocks it uses, so this script runs ouikit.py unchanged with one function replaced: the linked templates are left
out. Links to screens that are not built are pruned the way ouikit.py prunes them. Dependencies, the CSS slice,
--new-theme and --rename are ouikit.py's own code.

    python tests/bench/oui-reference.py --screens OuiTplWizard --new-theme BenchTheme --out <folder>

Takes the same options as ouikit.py and prints its JSON summary. Reads oui/oui-kit; writes only to --out.
"""
import pathlib
import sys

KIT = pathlib.Path(__file__).resolve().parents[2] / "oui" / "oui-kit" / "ouikit.py"
source = KIT.read_text(encoding="utf-8")
cut = source.index('if __name__ == "__main__":')
ns = {"__name__": "ouikit", "__file__": str(KIT), "__doc__": None}
exec(compile(source[:cut], str(KIT), "exec"), ns)  # the kit's functions
ns["with_templates"] = lambda lib, screens: set(screens)  # the one change: no linked templates
ns["__name__"] = "__main__"
sys.argv[0] = str(KIT)
exec(compile("\n" * source[:cut].count("\n") + source[cut:], str(KIT), "exec"), ns)  # the kit's own command line
