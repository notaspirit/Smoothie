from datetime import datetime
from pathlib import Path

class Logger:
    def __init__(self, log_dir: str):
        time_string = datetime.now().strftime("%Y-%m-%d_%H-%M-%S")
        path = Path(f"{Path(__file__).parent}\\{log_dir}\\{time_string}.txt")
        path.parent.mkdir(parents=True, exist_ok=True)

        self.file = open(path, "w", encoding="utf-8")

    def info(self, message: str):
        time_string = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
        message_formatted = f"{time_string} {message}"
        print(message_formatted)
        self.file.write(message_formatted + "\n")
        self.file.flush()

