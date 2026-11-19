document.addEventListener('click', event => {
    const button = event.target.closest('[data-repeat-add]');
    if (!button) return;

    const fieldset = button.closest('[data-repeat]');
    const rows = fieldset.querySelectorAll('[data-repeat-row]');
    if (rows.length >= 50) return;

    const row = rows[0].cloneNode(true);
    for (const input of row.querySelectorAll('input')) {
        input.name = input.name.replace(/\[0\]/, `[${rows.length}]`);
        input.value = input.defaultValue;
    }
    button.before(row);
    row.querySelector('input')?.focus();
    if (rows.length === 49) button.disabled = true;
});
