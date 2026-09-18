# Convenience targets around the fsmc CMake build (fsmc/CMakeLists.txt).
# Gives you g++-style one-liners from the repo root:
#
#   make                        # build the compiler into fsmc/build/
#   make compile INPUT=x.fsm    # compile a .fsm file (OUT=x.fsmb by default)
#   make compile INPUT=x.fsm OUT=/tmp/out.fsmb
#   make test                   # run all 13 CTest suites
#   make check                  # static checks over the Unity runtime + tests (see below)
#   make install                # copy fsmc to ~/.local/bin (plain `fsmc` on PATH)
#   make clean                  # remove fsmc/build/

BUILD_DIR := fsmc/build
FSMC      := $(BUILD_DIR)/fsmc
JOBS      ?= 4
# Needs tree-sitter for C#: python3 -m pip install tree_sitter tree_sitter_c_sharp
# (or point this at the interpreter that has it: make check PYTHON=/path/to/python)
PYTHON    ?= python3

.PHONY: all build test check compile install clean

all: build

build:
	cmake -S fsmc -B $(BUILD_DIR) -DCMAKE_BUILD_TYPE=Release
	cmake --build $(BUILD_DIR) -j $(JOBS)

test: build
	ctest --test-dir $(BUILD_DIR) --output-on-failure

# Static checks for the Unity runtime and the test cases: syntax of every .cs
# file, namespace usage, names used but declared nowhere (the CS0103 family -
# a renamed class with a caller left behind, or a helper file that never made it
# into a project), the 179-row function catalog and its dispatchers, and the
# committed generated classes against their .fsmb modules. Run this before
# copying the runtime into a project: it is what catches the errors Unity would
# otherwise report after the copy.
check:
	$(PYTHON) myFSM-UnityRuntime/Sandbox/check.py
	$(PYTHON) myFSM-UnityRuntime/Tests/Tools/verify_classes.py

# make compile INPUT=my_state.fsm [OUT=my_state.fsmb]
compile: build
	$(FSMC) $(INPUT) $(if $(OUT),-o $(OUT),)

install: build
	@mkdir -p "$(HOME)/.local/bin"
	cp $(FSMC) "$(HOME)/.local/bin/fsmc"
	@echo "Installed $(HOME)/.local/bin/fsmc"
	@echo "If 'fsmc' is not found in a new shell, add ~/.local/bin to your PATH:"
	@echo "  export PATH=\"\$HOME/.local/bin:\$$PATH\""

clean:
	rm -rf $(BUILD_DIR)
