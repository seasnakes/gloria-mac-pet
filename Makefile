# Shortcuts for build.sh. `make dmg` makes the installer other people download.
.PHONY: app install dmg icon clean
app:
	bash build.sh
install:
	bash build.sh --install
dmg:
	bash build.sh --dmg
icon:
	python3 tools/make_icon.py
clean:
	rm -rf GLORIA.app dist
