window.maiStudio = {
    applyTheme(theme) {
        document.documentElement.dataset.theme = theme;
        document.documentElement.style.colorScheme = theme;
    },
    toggleTheme() {
        const theme = document.documentElement.dataset.theme === "dark" ? "light" : "dark";
        this.applyTheme(theme);
        try {
            localStorage.setItem("mai-studio-theme", theme);
        } catch (error) {
            console.warn("The theme preference could not be stored.", error);
        }
    },
    async downloadFile(fileName, streamReference) {
        const content = await streamReference.arrayBuffer();
        const url = URL.createObjectURL(new Blob([content], { type: "application/epub+zip" }));
        const link = document.createElement("a");
        try {
            link.href = url;
            link.download = fileName;
            document.body.appendChild(link);
            link.click();
        } finally {
            link.remove();
            // Give the browser time to start reading the download before releasing it.
            setTimeout(() => URL.revokeObjectURL(url), 1000);
        }
    },
    showModal(dialog) {
        if (dialog && !dialog.open) {
            dialog.showModal();
        }
    }
};
(() => {
    let theme;
    try {
        theme = localStorage.getItem("mai-studio-theme");
    } catch (error) {
        console.warn("The theme preference could not be read.", error);
    }
    if (theme !== "light" && theme !== "dark") {
        theme = matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
    }
    window.maiStudio.applyTheme(theme);
})();
