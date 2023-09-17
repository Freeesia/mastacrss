#!/bin/bash

# 引数が存在するかどうかをチェック
if [ "$#" -eq 0 ]; then
    # 引数がない場合、supervisordを起動
    exec supervisord -c /etc/supervisord.conf
else
    # 引数がある場合、dotnetに引数を渡して起動
    exec dotnet mastacrss.dll "$@"
fi
