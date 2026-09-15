DOTNET = dotnet
CONFIG = Debug

.PHONY: all
all: run

.PHONY: run
run:
	$(DOTNET) run -c $(CONFIG) -v q

.PHONY: build
build:
	$(DOTNET) build -c $(CONFIG) -v q

