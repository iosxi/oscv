@echo off
cd "C:\Program Files (x86)\LG Electronics\OnScreen Control\bin"
@rem  osccli -c run
@rem  timeout /t 3 > nul

for /f "tokens=1 delims= " %%a in ('osccli -c list ^| findstr /i "LG.*4K"') do (
    set "monitor_id=%%a"
    goto :next_command
)

:next_command
echo モニターID: %monitor_id%
rem %monitor_id% を使って次のコマンドを実行する

osccli -c brightness      -t %monitor_id% -o set 65
osccli -c contrast        -t %monitor_id% -o set 75
osccli -c blackstabilizer -t %monitor_id% -o set 13
rem osccli -c responsetime    -t %monitor_id% -o set Fast
@rem  osccli -c exit

