add_executable(encspot EncSpotConsole.cpp)
target_link_libraries(encspot libencspot)
install(TARGETS encspot DESTINATION bin)
