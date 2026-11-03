/// Adds two numbers.
pub fn add(left:i32,right:i32)->i32{left+right}

#[cfg(test)]
mod tests {
    use super::add;

    #[test]
    fn add_sums_two_numbers() {
        assert_eq!(add(2, 3), 5);
    }
}
