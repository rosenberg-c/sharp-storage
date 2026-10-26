DOTNET ?= dotnet
PROJECT ?= SharpStorage.csproj
TESTS ?= tests/SharpStorage.Tests.csproj

.PHONY: build test lint format clean
build:
	$(DOTNET) build $(PROJECT)

test:
	$(DOTNET) test $(TESTS)

lint:
	$(DOTNET) format $(PROJECT) --verify-no-changes
	$(DOTNET) format $(TESTS) --verify-no-changes

format:
	$(DOTNET) format $(PROJECT)
	$(DOTNET) format $(TESTS)

clean:
	$(DOTNET) clean $(PROJECT)
	$(DOTNET) clean $(TESTS)
