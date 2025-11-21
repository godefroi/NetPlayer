#! /bin/bash

dotnet publish src/SongServer.Player \
	--configuration Release \
	--output ./published \
	--self-contained

dotnet publish src/SongServer.Ui \
	--configuration Debug \
	--output ./published
