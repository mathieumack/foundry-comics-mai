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
