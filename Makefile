ASM_NAME = "ipk25-chat"

BUILD: CLEAN *.csproj
	dotnet publish -c Release -o .

CLEAN:
	dotnet restore
	dotnet clean
	rm -rf bin obj $(ASM_NAME) $(ASM_NAME).pdb

ZIP: CLEAN
	zip -r xzhdan00.zip . -x "*.git*" -x "*.idea*"