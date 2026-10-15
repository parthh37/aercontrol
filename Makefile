# AerControl: common tasks. Windows users: see windows/scripts/*.ps1.
.PHONY: help test mac-test win-test mac-cli mac-app mac-run mac-xcode mac-release icons clean

help:
	@echo "make test         run all unit tests (Swift + .NET)"
	@echo "make mac-cli      build aerctl (CLI) → macos/.build/release/aerctl"
	@echo "make mac-app      build AerControl.app with SwiftPM → macos/dist/ (no widget, ad-hoc signed)"
	@echo "make mac-run      build and open the dev app"
	@echo "make mac-xcode    generate + open the Xcode project (app + widget)"
	@echo "make mac-release  signed + notarized build (needs DEVELOPMENT_TEAM)"
	@echo "make icons        regenerate icons from tools/gen_icons.py"

test: mac-test win-test

mac-test:
	cd macos && swift test

win-test:
	cd windows && dotnet test AerControl.Portable.slnf

mac-cli:
	cd macos && swift build -c release --product aerctl
	@echo "→ macos/.build/release/aerctl"

mac-app:
	macos/scripts/bundle-dev-app.sh

mac-run: mac-app
	open macos/dist/AerControl.app

mac-xcode:
	cd macos && xcodegen generate && open AerControl.xcodeproj

mac-release:
	cd macos && ./scripts/release.sh

icons:
	python3 tools/gen_icons.py

clean:
	rm -rf macos/.build macos/dist macos/build macos/AerControl.xcodeproj
	cd windows && dotnet clean AerControl.Portable.slnf >/dev/null
