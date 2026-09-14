# Convenience targets around the fsmc CMake build (fsmc/CMakeLists.txt).
# Gives you g++-style one-liners from the repo root:
#
#   make                        # build the compiler into fsmc/build/
#   make compile INPUT=x.fsm    # compile a .fsm file (OUT=x.fsmb by default)
#   make compile INPUT=x.fsm OUT=/tmp/out.fsmb
#   make test                   # run all 12 CTest suites
#   make install                # copy fsmc to ~/.local/bin (plain `fsmc` on PATH)
#   make clean                  # remove fsmc/build/

BUILD_DIR := fsmc/build
FSMC      := $(BUILD_DIR)/fsmc
JOBS      ?= 4

.PHONY: all build test compile install clean

all: build

build:
	cmake -S fsmc -B $(BUILD_DIR) -DCMAKE_BUILD_TYPE=Release
	cmake --build $(BUILD_DIR) -j $(JOBS)

test: build
	ctest --test-dir $(BUILD_DIR) --output-on-failure

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
