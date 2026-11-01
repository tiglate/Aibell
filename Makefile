# Thin proxy to ./make.sh so that "make <action>" works.
# make.sh stays the single source of truth for what each action does.
#
#   make run
#   make test ARGS="--filter FullyQualifiedName~Flac"
#   make publish RID=win-x64
#   make build CONFIGURATION=Release
#
# CONFIGURATION and RID given on the make command line or in the environment
# are exported to make.sh. Extra arguments go through ARGS.

ACTIONS := build run test coverage secrets radio-check format clean publish deb appimage flatpak snap msi

.PHONY: $(ACTIONS) help
.DEFAULT_GOAL := help

# Command-line variables are exported to the recipe environment.
export CONFIGURATION
export RID

$(ACTIONS):
	@./make.sh $@ $(ARGS)

help:
	@./make.sh || true
